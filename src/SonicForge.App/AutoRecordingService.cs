using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;

namespace SonicForge.App;

public sealed class AutoRecordingStatusEventArgs(string text, float left, float right) : EventArgs
{ public string Text { get; }=text; public float Left { get; }=left; public float Right { get; }=right; }

public sealed class AutoRecordingService : IDisposable
{
    private WasapiLoopbackCapture? _capture; private WaveFileWriter? _writer;
    private readonly Queue<byte[]> _preBuffer=new(); private int _preBytes; private string? _tempWav; private string? _mp3;
    private CurrentMediaInfo _mediaInfo = new("", "", "");
    private double _signalSeconds; private double _silenceSeconds; private bool _recording; private bool _stopping;
    public event EventHandler<AutoRecordingStatusEventArgs>? StatusChanged;
    public event EventHandler<string>? Mp3Saved;
    public bool IsActive => _capture is not null; public bool IsRecording => _recording;
    public bool IsRestartPending => _phase3cRestartRequested;
    public double StartThresholdDb {get;set;}=-38; public double StopThresholdDb {get;set;}=-45;
    public double StartConfirmSeconds {get;set;}=.4; public double StopSilenceSeconds {get;set;}=4; public double PreRollSeconds {get;set;}=1.5;

    public void Start(string deviceId,string outputFolder)
    {
        if(IsActive) throw new InvalidOperationException("Automatik ist bereits aktiv.");
        Phase3cInitialize(deviceId, outputFolder);
        Directory.CreateDirectory(outputFolder);
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDevice(deviceId);
        _mediaInfo = WindowsMediaMetadataService.GetCurrentAsync().GetAwaiter().GetResult();
        _capture=new WasapiLoopbackCapture(dev); var artist = WindowsMediaMetadataService.SafeFilePart(_mediaInfo.Artist);
        var title = WindowsMediaMetadataService.SafeFilePart(_mediaInfo.Title);
        var name = !string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}")
            : "SonicForge-Auto-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _tempWav=Path.Combine(outputFolder,name+".tmp.wav"); _mp3=Path.Combine(outputFolder,name+".mp3");
        _capture.DataAvailable+=OnData; _capture.RecordingStopped+=OnStopped; Phase3a2Reset(); _capture.StartRecording();
        Raise("Warte auf Musik ...",0,0);
    }
    public void Stop(){if(_capture is null||_stopping)return;_stopping=true;_capture.StopRecording();}
    private void OnData(object? s,WaveInEventArgs e)
    {
        if(_capture is null)return; var (l,r)=Peak(e.Buffer,e.BytesRecorded,_capture.WaveFormat); var peak=Math.Max(l,r);
        var db=peak<=.000001?-96:20*Math.Log10(peak); var seconds=(double)e.BytesRecorded/_capture.WaveFormat.AverageBytesPerSecond;
        Phase3a2Observe(db);
        Phase3cObserveTitleChange();
        Phase3c1ApplyPendingSplit();
        if(!_recording)
        {
            AddPre(e.Buffer,e.BytesRecorded,_capture.WaveFormat.AverageBytesPerSecond);
            _signalSeconds=db>=StartThresholdDb?_signalSeconds+seconds:0;
            Raise(_signalSeconds>0?$"Musik erkannt: {_signalSeconds:0.0} s":"Warte auf Musik ...",l,r);
            if(_signalSeconds>=StartConfirmSeconds)
            {
                _writer=new WaveFileWriter(_tempWav!,_capture.WaveFormat); foreach(var b in _preBuffer)_writer.Write(b,0,b.Length);
                _preBuffer.Clear();_preBytes=0;_recording=true;_silenceSeconds=0; Raise("Musik erkannt, Aufnahme lÃ¤uft.",l,r);
            }
            return;
        }
        _writer!.Write(e.Buffer,0,e.BytesRecorded);
        // PHASE3C2B_CONFIGURED_STOP_FIX
        // Die im Fenster eingestellte Stoppzeit wird verwendet. Im Hysteresebereich
        // bleibt der Zaehler stabil, statt rueckwaerts zu laufen und haengenzubleiben.
        var effectiveStopSeconds = Math.Max(1.0, StopSilenceSeconds);
        var resetDb = StopThresholdDb + 3.0;
        if (db < StopThresholdDb)
            _silenceSeconds += seconds;
        else if (db >= resetDb)
            _silenceSeconds = 0;
        Raise(_silenceSeconds > 0
            ? $"Leise Passage wird geprueft: {_silenceSeconds:0.0} / {effectiveStopSeconds:0.0} s"
            : "Automatische MP3-Aufnahme laeuft.", l, r);
        if (_silenceSeconds >= effectiveStopSeconds) { _phase3cRestartRequested = true; Raise("Titelpause erkannt. Speichere Titel und warte weiter ...",l,r); Stop(); }
    }
    private void AddPre(byte[] src,int count,int bytesPerSecond)
    {
        var copy=new byte[count];Buffer.BlockCopy(src,0,copy,0,count);_preBuffer.Enqueue(copy);_preBytes+=count;
        var max=(int)(bytesPerSecond*PreRollSeconds);while(_preBytes>max&&_preBuffer.Count>0)_preBytes-=_preBuffer.Dequeue().Length;
    }
    private async void OnStopped(object? s,StoppedEventArgs e)
    {
        _writer?.Dispose();_writer=null; if(_capture is not null){_capture.DataAvailable-=OnData;_capture.RecordingStopped-=OnStopped;_capture.Dispose();_capture=null;}
        if(e.Exception is not null){Raise("Fehler: "+e.Exception.Message,0,0);Reset();return;}
        if(!_recording||string.IsNullOrWhiteSpace(_tempWav)||!File.Exists(_tempWav)){Raise("Automatik beendet. Keine Musik aufgenommen.",0,0);Reset();return;}
        try
        {
            Raise("Analysiere Aufnahmequalitaet ...",0,0); var wav=_tempWav;var mp3=_mp3;
            double phase3bPeakDb = -96.0; double phase3bGainDb = 0.0; bool phase3bClipped = false;
            await Task.Run(() =>
            {
                // PHASE3B_QUALITY_OPTIMIZATION
                using (var phase3bReader = new AudioFileReader(wav))
                {
                    var phase3bBuffer = new float[Math.Max(4096, phase3bReader.WaveFormat.SampleRate * phase3bReader.WaveFormat.Channels / 10)];
                    float phase3bPeak = 0f; int phase3bRead;
                    while ((phase3bRead = phase3bReader.Read(phase3bBuffer, 0, phase3bBuffer.Length)) > 0)
                    {
                        for (var i = 0; i < phase3bRead; i++)
                        {
                            var sample = Math.Abs(phase3bBuffer[i]);
                            if (sample > phase3bPeak) phase3bPeak = sample;
                            if (sample >= 0.9999f) phase3bClipped = true;
                        }
                    }
                    phase3bPeakDb = phase3bPeak <= 0.000001f ? -96.0 : 20.0 * Math.Log10(phase3bPeak);
                    var phase3bTargetDb = -1.5;
                    phase3bGainDb = phase3bClipped ? 0.0 : Math.Clamp(phase3bTargetDb - phase3bPeakDb, 0.0, 12.0);
                }
                using var reader = new AudioFileReader(wav);
                reader.Volume = (float)Math.Pow(10.0, phase3bGainDb / 20.0);
                ISampleProvider stereoSource;
                if (reader.WaveFormat.Channels == 1)
                {
                    stereoSource = new MonoToStereoSampleProvider(reader);
                }
                else if (reader.WaveFormat.Channels == 2)
                {
                    stereoSource = reader;
                }
                else
                {
                    var mux = new MultiplexingSampleProvider(new[] { (ISampleProvider)reader }, 2);
                    mux.ConnectInputToOutput(0, 0);
                    mux.ConnectInputToOutput(1, 1);
                    stereoSource = mux;
                }
                var pcm16 = stereoSource.ToWaveProvider16();
                using var writer = new LameMP3FileWriter(mp3!, pcm16.WaveFormat, 320);
                var buffer = new byte[64 * 1024];
                int bytesRead;
                while ((bytesRead = pcm16.Read(buffer, 0, buffer.Length)) > 0)
                {
                    writer.Write(buffer, 0, bytesRead);
                }
            });
            using (var tagFile = global::TagLib.File.Create(mp3!))
            {
                if (!string.IsNullOrWhiteSpace(_mediaInfo.Title)) tagFile.Tag.Title = _mediaInfo.Title;
                if (!string.IsNullOrWhiteSpace(_mediaInfo.Artist)) tagFile.Tag.Performers = new[] { _mediaInfo.Artist };
                if (!string.IsNullOrWhiteSpace(_mediaInfo.Album)) tagFile.Tag.Album = _mediaInfo.Album;
                tagFile.Tag.Comment = "Automatisch aufgenommen mit SonicForge";
                tagFile.Save();
            }
            // PHASE3C2C_AUTO_COVER
            // PHASE3C2C_FIX3_DEEZER_BACKGROUND
            // Cover vollständig einbetten, bevor die MP3 als gespeichert gemeldet wird.
            // Dadurch sieht der unmittelbar geöffnete Tag-Editor das neue APIC-Bild zuverlässig.
            var coverResult = await CoverArtService.TryEmbedAsync(mp3!, _mediaInfo.Title, _mediaInfo.Artist);
            Raise("Cover: " + coverResult, 0, 0);
            if(phase3bClipped) Raise("Hinweis: Quelle enthaelt bereits Uebersteuerungen. Keine Verstaerkung angewendet.",0,0);
            else if(phase3bGainDb>=0.1) Raise($"Pegel optimiert: {phase3bPeakDb:0.0} dBFS, Verstaerkung +{phase3bGainDb:0.0} dB.",0,0);
            else Raise($"Aufnahmepegel bereits optimal: {phase3bPeakDb:0.0} dBFS.",0,0);
            File.Delete(wav);Raise("MP3 gespeichert: "+Path.GetFileName(mp3),0,0);NotifyMp3Saved(mp3!);
        }catch(Exception ex){Raise("MP3-Fehler: "+ex.Message,0,0);}finally{var restart=_phase3cRestartRequested;Reset();if(restart)Phase3cRestart();}
    }
    private void Reset(){_recording=false;_stopping=false;_signalSeconds=0;_silenceSeconds=0;_preBuffer.Clear();_preBytes=0;}
    private void NotifyMp3Saved(string mp3Path)
    {
        Mp3Saved?.Invoke(this, mp3Path);
    }
    private void Raise(string t,float l,float r)=>StatusChanged?.Invoke(this,new(t,l,r));
    private static (float,float) Peak(byte[] b,int count,WaveFormat f)
    {
        float l=0,r=0;int ch=Math.Max(1,f.Channels);
        if(f.Encoding==WaveFormatEncoding.IeeeFloat&&f.BitsPerSample==32){for(int i=0;i<count/4;i++){var v=Math.Abs(BitConverter.ToSingle(b,i*4));if(i%ch==0)l=Math.Max(l,v);else r=Math.Max(r,v);}}
        else if(f.BitsPerSample==16){for(int i=0;i<count/2;i++){var v=Math.Abs(BitConverter.ToInt16(b,i*2)/32768f);if(i%ch==0)l=Math.Max(l,v);else r=Math.Max(r,v);}}
        if(ch==1)r=l;return(Math.Clamp(l,0,1),Math.Clamp(r,0,1));
    }
    public void Dispose(){try{Stop();}catch{} _writer?.Dispose();_capture?.Dispose();}

    // PHASE3A2_AUTO_THRESHOLD
    private readonly object _phase3a2Gate = new();
    private DateTime _phase3a2StartedUtc;
    private double _phase3a2NoiseDb = -96.0;
    private bool _phase3a2Ready;
    public bool AutomaticThresholdsEnabled { get; set; } = true;
    public bool AutomaticThresholdsCalibrated => _phase3a2Ready;
    public double AutomaticNoiseFloorDb => _phase3a2NoiseDb;

    private void Phase3a2Reset()
    {
        lock (_phase3a2Gate)
        {
            _phase3a2StartedUtc = DateTime.UtcNow;
            _phase3a2NoiseDb = -96.0;
            _phase3a2Ready = !AutomaticThresholdsEnabled;
            if (AutomaticThresholdsEnabled)
            {
                StartThresholdDb = 1.0;
                StopThresholdDb = 0.0;
            }
        }
    }

    private void Phase3a2Observe(double db)
    {
        if (!AutomaticThresholdsEnabled) return;
        lock (_phase3a2Gate)
        {
            if (_phase3a2Ready || double.IsNaN(db) || double.IsInfinity(db)) return;
            if (db > _phase3a2NoiseDb && db < -18.0) _phase3a2NoiseDb = db;
            if ((DateTime.UtcNow - _phase3a2StartedUtc).TotalSeconds < 2.0) return;
            StartThresholdDb = Math.Clamp(_phase3a2NoiseDb + 18.0, -52.0, -24.0);
            StopThresholdDb = Math.Clamp(_phase3a2NoiseDb + 10.0, -60.0, StartThresholdDb - 6.0);
            _phase3a2Ready = true;
        }
    }
    // PHASE3C_TITLE_CHANGE_SPLIT
    private readonly object _phase3cGate = new();
    private string? _phase3cDeviceId;
    private string? _phase3cOutputFolder;
    private DateTime _phase3cRecordingStartedUtc;
    private DateTime _phase3cLastPollUtc;
    private string _phase3cCandidateKey = "";
    private int _phase3cCandidateCount;
    private bool _phase3cPollRunning;
    private bool _phase3cRestartRequested;
    public bool SplitOnTitleChangeEnabled { get; set; } = true;
    public double TitleChangeConfirmSeconds { get; set; } = 3.0;
    public double MinimumTrackSeconds { get; set; } = 8.0;

    private void Phase3cInitialize(string deviceId, string outputFolder)
    {
        lock (_phase3cGate)
        {
            _phase3cDeviceId = deviceId;
            _phase3cOutputFolder = outputFolder;
            _phase3cRecordingStartedUtc = DateTime.UtcNow;
            _phase3cLastPollUtc = DateTime.MinValue;
            _phase3cCandidateKey = "";
            _phase3cCandidateCount = 0;
            _phase3cPollRunning = false;
            _phase3cRestartRequested = false;
        }
    }

    private void Phase3cObserveTitleChange()
    {
        if (!SplitOnTitleChangeEnabled || !_recording || _stopping) return;
        if ((DateTime.UtcNow - _phase3cRecordingStartedUtc).TotalSeconds < MinimumTrackSeconds) return;
        lock (_phase3cGate)
        {
            if (_phase3cPollRunning || (DateTime.UtcNow - _phase3cLastPollUtc).TotalSeconds < 1.0) return;
            _phase3cPollRunning = true;
            _phase3cLastPollUtc = DateTime.UtcNow;
        }
        _ = Phase3cCheckMetadataAsync();
    }

    private async Task Phase3cCheckMetadataAsync()
    {
        try
        {
            var current = await WindowsMediaMetadataService.GetCurrentAsync().ConfigureAwait(false);
            if (!current.HasUsefulData) return;
            var oldKey = Phase3cKey(_mediaInfo);
            var newKey = Phase3cKey(current);
            if (string.IsNullOrWhiteSpace(newKey) || newKey == oldKey)
            {
                lock (_phase3cGate) { _phase3cCandidateKey = ""; _phase3cCandidateCount = 0; }
                return;
            }
            bool confirmed = false;
            lock (_phase3cGate)
            {
                if (_phase3cCandidateKey == newKey) _phase3cCandidateCount++;
                else { _phase3cCandidateKey = newKey; _phase3cCandidateCount = 1; }
                confirmed = _phase3cCandidateCount >= Math.Max(2, (int)Math.Ceiling(TitleChangeConfirmSeconds));
                if (confirmed && !_phase3cRestartRequested)
                {
                    _phase3cRestartRequested = true;
                }
                else confirmed = false;
            }
            if (confirmed)
            {
                lock (_phase3cGate) _phase3c1PendingInfo = current;
                Raise("Titelwechsel bestaetigt. Trenne Aufnahme ohne Unterbrechung ...",0,0);
            }
        }
        catch { }
        finally { lock (_phase3cGate) _phase3cPollRunning = false; }
    }

    private static string Phase3cKey(CurrentMediaInfo info)
        => (info.Artist.Trim() + "\n" + info.Title.Trim()).ToUpperInvariant();

    private void Phase3cRestart()
    {
        var deviceId = _phase3cDeviceId;
        var outputFolder = _phase3cOutputFolder;
        _phase3cRestartRequested = false;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(outputFolder)) return;
        try
        {
            Start(deviceId, outputFolder);
            Raise("Neuer Titel erkannt. Automatikaufnahme fortgesetzt.",0,0);
        }
        catch (Exception ex)
        {
            Raise("Neustart nach Titelwechsel fehlgeschlagen: " + ex.Message,0,0);
        }
    }
    // PHASE3C1_HOT_SPLIT
    private CurrentMediaInfo? _phase3c1PendingInfo;

    private void Phase3c1ApplyPendingSplit()
    {
        CurrentMediaInfo? next;
        lock (_phase3cGate)
        {
            next = _phase3c1PendingInfo;
            _phase3c1PendingInfo = null;
        }
        if (next is null || !_recording || _writer is null || _capture is null) return;

        var oldWriter = _writer;
        var oldWav = _tempWav;
        var oldMp3 = _mp3;
        var oldInfo = _mediaInfo;
        oldWriter.Dispose();

        var folder = _phase3cOutputFolder!;
        var artist = WindowsMediaMetadataService.SafeFilePart(next.Artist);
        var title = WindowsMediaMetadataService.SafeFilePart(next.Title);
        var name = !string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}")
            : "SonicForge-Auto-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var newMp3 = Path.Combine(folder,name+".mp3");
        if (File.Exists(newMp3)) name += "-" + DateTime.Now.ToString("HH-mm-ss");
        _tempWav = Path.Combine(folder,name+".tmp.wav");
        _mp3 = Path.Combine(folder,name+".mp3");
        _mediaInfo = next;
        _writer = new WaveFileWriter(_tempWav,_capture.WaveFormat);
        _silenceSeconds = 0;
        _signalSeconds = 0;
        _phase3cRecordingStartedUtc = DateTime.UtcNow;
        _phase3cCandidateKey = "";
        _phase3cCandidateCount = 0;
        _phase3cRestartRequested = false;
        Raise("Neuer Titel wird aufgenommen: " + name,0,0);

        if (!string.IsNullOrWhiteSpace(oldWav) && !string.IsNullOrWhiteSpace(oldMp3))
            _ = Phase3c1FinalizeSplitAsync(oldWav,oldMp3,oldInfo);
    }

    private async Task Phase3c1FinalizeSplitAsync(string wav,string mp3,CurrentMediaInfo info)
    {
        try
        {
            double peakDb = -96.0; double gainDb = 0.0; bool clipped = false;
            await Task.Run(() =>
            {
                using (var scan = new AudioFileReader(wav))
                {
                    var samples = new float[Math.Max(4096,scan.WaveFormat.SampleRate*scan.WaveFormat.Channels/10)];
                    float peak = 0f; int read;
                    while ((read=scan.Read(samples,0,samples.Length))>0)
                        for (var i=0;i<read;i++) { var sample=Math.Abs(samples[i]); if(sample>peak)peak=sample; if(sample>=0.9999f)clipped=true; }
                    peakDb=peak<=0.000001f?-96.0:20.0*Math.Log10(peak);
                    gainDb=clipped?0.0:Math.Clamp(-1.5-peakDb,0.0,12.0);
                }
                using var reader=new AudioFileReader(wav);
                reader.Volume=(float)Math.Pow(10.0,gainDb/20.0);
                ISampleProvider stereo;
                if(reader.WaveFormat.Channels==1)stereo=new MonoToStereoSampleProvider(reader);
                else if(reader.WaveFormat.Channels==2)stereo=reader;
                else { var mux=new MultiplexingSampleProvider(new[]{(ISampleProvider)reader},2);mux.ConnectInputToOutput(0,0);mux.ConnectInputToOutput(1,1);stereo=mux; }
                var pcm16=stereo.ToWaveProvider16();
                using var writer=new LameMP3FileWriter(mp3,pcm16.WaveFormat,320);
                var buffer=new byte[64*1024];int bytesRead;
                while((bytesRead=pcm16.Read(buffer,0,buffer.Length))>0)writer.Write(buffer,0,bytesRead);
            });
            using(var tagFile=global::TagLib.File.Create(mp3))
            {
                if(!string.IsNullOrWhiteSpace(info.Title))tagFile.Tag.Title=info.Title;
                if(!string.IsNullOrWhiteSpace(info.Artist))tagFile.Tag.Performers=new[]{info.Artist};
                if(!string.IsNullOrWhiteSpace(info.Album))tagFile.Tag.Album=info.Album;
                tagFile.Tag.Comment="Automatisch aufgenommen mit SonicForge";tagFile.Save();
            }
            // Auch automatisch getrennte Titel erhalten vor der Fertigmeldung ihr Cover.
            var splitCoverResult = await CoverArtService.TryEmbedAsync(mp3, info.Title, info.Artist);
            Raise("Cover: " + splitCoverResult, 0, 0);
            File.Delete(wav);
            Raise("Getrennter Titel gespeichert: "+Path.GetFileName(mp3),0,0);
            NotifyMp3Saved(mp3);
        }
        catch(Exception ex) { Raise("Titeltrennung fehlgeschlagen: "+ex.Message,0,0); }
    }}
