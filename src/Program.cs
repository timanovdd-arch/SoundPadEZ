using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SimpleSoundpad;

static class Program
{
    public static readonly string Home = AppContext.BaseDirectory;
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--check")) { Diagnostics.Run(); return; }
        if (args.Contains("--audio-test")) { Diagnostics.AudioTest(); return; }
        if (args.Contains("--hotkey-check")) { GlobalHotkeys.Check(); return; }
        if (args.Contains("--library-check")) { PadForm.CheckLibrary(); return; }
        if (args.Contains("--snapshot"))
        {
            using var form = new PadForm();
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
                bitmap.Save(Path.Combine(Home, "window-check.png"));
                form.Close();
            });
            Application.Run(form);
            return;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Soundpad");
        Application.Run(new PadForm());
    }
}

sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

sealed class Settings
{
    public string Microphone { get; set; } = "";
    public string Output { get; set; } = "";
    public int Volume { get; set; } = 70;
    public bool Voice { get; set; } = true;
    public List<string> Favorites { get; set; } = [];
    public string?[] Hotkeys { get; set; } = new string?[5];
}

// Playback and conversion use NAudio's MIT-licensed AudioFileReader,
// WASAPI and sample providers: https://github.com/naudio/NAudio
sealed class AudioEngine : ISampleProvider, IDisposable
{
    private readonly object gate = new();
    private WasapiOut? output;
    private WasapiCapture? capture;
    private MMDevice? outputDevice, inputDevice;
    private BufferedWaveProvider? microphoneBuffer;
    private ISampleProvider? microphone, clip;
    private AudioFileReader? reader;
    private float[] scratch = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public float Volume { get; set; } = .7f;
    public bool Voice { get; set; } = true;
    public float MicrophonePeak { get; private set; }
    public bool Playing { get { lock (gate) return reader != null; } }
    public string? Error { get; private set; }

    public void Start(string microphoneId, string outputId, bool withVoice)
    {
        Dispose();
        Error = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            outputDevice = devices.GetDevice(outputId);
            if (withVoice)
            {
                inputDevice = devices.GetDevice(microphoneId);
                if (inputDevice.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выбери настоящий микрофон, а не CABLE Output: иначе будет эхо.");
                capture = new WasapiCapture(inputDevice);
                microphoneBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(400),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                microphone = Convert(microphoneBuffer.ToSampleProvider());
                capture.DataAvailable += (_, e) =>
                {
                    lock (gate)
                    {
                        if (microphoneBuffer.BufferedDuration.TotalMilliseconds > 180)
                            microphoneBuffer.ClearBuffer();
                        microphoneBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    }
                };
                capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error = e.Exception.ToString(); };
                capture.StartRecording();
            }
            output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 60);
            output.PlaybackStopped += (_, e) => { if (e.Exception != null) Error = e.Exception.ToString(); };
            output.Init(new FloatWaveProvider(this));
            output.Play();
        }
        catch { Dispose(); throw; }
    }

    private static ISampleProvider Convert(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels != 2)
            throw new NotSupportedException("Нужен моно или стерео аудиофайл/микрофон.");
        if (source.WaveFormat.SampleRate != 48000) source = new WdlResamplingSampleProvider(source, 48000);
        return source;
    }

    public void Play(string path)
    {
        var next = new AudioFileReader(path);
        ISampleProvider converted;
        try { converted = Convert(next); }
        catch { next.Dispose(); throw; }
        lock (gate)
        {
            reader?.Dispose();
            reader = next;
            clip = converted;
        }
    }
    public void Stop()
    {
        lock (gate) { clip = null; reader?.Dispose(); reader = null; }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            Array.Clear(buffer, offset, count);
            if (scratch.Length < count) scratch = new float[count];
            if (microphone != null)
            {
                int n = microphone.Read(scratch, 0, count);
                float peak = 0;
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(scratch[i]));
                MicrophonePeak = Math.Max(peak, MicrophonePeak * .96f);
                if (Voice) Array.Copy(scratch, 0, buffer, offset, n);
            }
            if (clip != null)
            {
                int n = clip.Read(scratch, 0, count);
                float gain = Volume;
                for (int i = 0; i < n; i++) buffer[offset + i] += scratch[i] * gain;
                if (n < count) { clip = null; reader?.Dispose(); reader = null; }
            }
            for (int i = offset; i < offset + count; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return count;
        }
    }
    public void Dispose()
    {
        // Stop callbacks before acquiring the mixer lock.
        output?.Dispose(); output = null;
        capture?.Dispose(); capture = null;
        lock (gate) { Stop(); microphone = null; microphoneBuffer = null; MicrophonePeak = 0; }
        inputDevice?.Dispose(); inputDevice = null;
        outputDevice?.Dispose(); outputDevice = null;
    }
}

// Keep real float arrays at the mixer boundary. NAudio's WaveBuffer adapter
// aliases byte[] as float[], which is incompatible with Array.Copy/Clear.
sealed class FloatWaveProvider(ISampleProvider source) : IWaveProvider
{
    private float[] samples = [];
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(byte[] buffer, int offset, int count)
    {
        int wanted = count / sizeof(float);
        if (samples.Length < wanted) samples = new float[wanted];
        int read = source.Read(samples, 0, wanted);
        Buffer.BlockCopy(samples, 0, buffer, offset, read * sizeof(float));
        return read * sizeof(float);
    }
}

sealed class PadForm : Form
{
    readonly ListBox files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ComboBox mic = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox destination = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox voice = new() { Text = "Добавлять мой голос", AutoSize = true };
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Dock = DockStyle.Fill };
    readonly Label volumeLabel = new() { AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label microphoneLevel = new() { AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
    readonly ComboBox category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    readonly ComboBox hotkeySlot = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly Button favoriteButton = new() { Text = "★ В избранное", AutoSize = true };
    readonly HashSet<int> registeredHotkeys = [];
    IntPtr hotkeyWindow;
    readonly AudioEngine engine = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    readonly string soundFolder = Path.Combine(Program.Home, "sounds");
    readonly string settingsPath = Path.Combine(Program.Home, "settings.json");
    Settings settings = new();
    bool connected, wasPlaying, refreshingDevices, enterHeld;
    WasapiOut? preview;
    AudioFileReader? previewReader;
    string? nowPlaying;
    static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mp3", ".wav", ".m4a", ".aac", ".wma", ".aiff" };

    public PadForm(string? dataDirectory = null)
    {
        if (dataDirectory != null)
        {
            soundFolder = Path.Combine(dataDirectory, "sounds");
            settingsPath = Path.Combine(dataDirectory, "settings.json");
        }
        Text = "Простой Soundpad";
        ClientSize = new Size(720, 590);
        MinimumSize = new Size(700, 570);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        Directory.CreateDirectory(soundFolder);
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? new(); } catch { }
        settings.Favorites ??= [];
        settings.Hotkeys = Enumerable.Range(0, 5).Select(i => settings.Hotkeys?.ElementAtOrDefault(i)).ToArray();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 11 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 11; i++) layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.Absolute, i == 5 ? 100 : i == 2 ? 37 : i == 7 ? 42 : i == 9 ? 50 : 36));
        Add(new Label { Text = "Микрофон:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        Add(mic, 1, 0);
        Add(new Label { Text = "Вывод звука:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        Add(destination, 1, 1);
        var connection = new FlowLayoutPanel { Dock = DockStyle.Fill };
        connection.Controls.Add(voice);
        ButtonInto(connection, "Подключить", Connect);
        ButtonInto(connection, "Обновить устройства", RefreshDevices);
        connection.Controls.Add(microphoneLevel);
        Add(connection, 0, 2, 2);
        var folderButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ButtonInto(folderButtons, "Открыть папку sounds", () => Process.Start(new ProcessStartInfo(soundFolder) { UseShellExecute = true }));
        ButtonInto(folderButtons, "Обновить список", () => RefreshFiles());
        Add(folderButtons, 0, 3, 2);
        var categories = new FlowLayoutPanel { Dock = DockStyle.Fill };
        category.Items.AddRange(["Все звуки", "★ Избранное"]); category.SelectedIndex = 0;
        categories.Controls.Add(category); categories.Controls.Add(favoriteButton);
        favoriteButton.Click += (_, _) => ToggleFavorite();
        category.SelectedIndexChanged += (_, _) => RefreshFiles(true);
        Add(categories, 0, 4, 2);
        files.FormattingEnabled = true;
        files.Format += (_, e) =>
        {
            if (e.ListItem is not string name) return;
            int slot = Array.FindIndex(settings.Hotkeys, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            e.Value = (IsFavorite(name) ? "★ " : "") + name + (slot >= 0 ? $"    [{GlobalHotkeys.Name(slot)}]" : "");
        };
        files.SelectedIndexChanged += (_, _) => UpdateFavoriteButton();
        Add(files, 0, 5, 2);
        var assignments = new FlowLayoutPanel { Dock = DockStyle.Fill };
        assignments.Controls.Add(new Label { Text = "Хоткей:", AutoSize = true, Margin = new Padding(0, 7, 3, 0) });
        for (int i = 0; i < 5; i++) hotkeySlot.Items.Add(GlobalHotkeys.Name(i));
        hotkeySlot.SelectedIndex = 0;
        assignments.Controls.Add(hotkeySlot);
        ButtonInto(assignments, "Назначить выбранному звуку", AssignHotkey);
        ButtonInto(assignments, "Очистить", () => { settings.Hotkeys[hotkeySlot.SelectedIndex] = null; Save(); RefreshFiles(true); });
        Add(assignments, 0, 6, 2);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ButtonInto(controls, "▶ Играть", Play);
        ButtonInto(controls, "■ Стоп", () => { engine.Stop(); StopPreview(); status.Text = connected ? "Подключено. Голос продолжает работать." : "Остановлено."; });
        ButtonInto(controls, "Только у себя", Preview);
        Add(controls, 0, 7, 2);
        Add(volumeLabel, 0, 8); Add(volume, 1, 8);
        Add(new Label { Dock = DockStyle.Fill, Text = "В Discord / игре микрофон: CABLE Output.\nХоткеи Ctrl+Alt+1…5 работают и при свёрнутом окне.", AutoSize = false }, 0, 9, 2);
        Add(status, 0, 10, 2);
        Controls.Add(layout);
        volume.Value = Math.Clamp(settings.Volume, 0, 100);
        voice.Checked = settings.Voice;
        UpdateVolume();
        volume.ValueChanged += (_, _) => { UpdateVolume(); Save(); };
        voice.CheckedChanged += (_, _) => SelectionChanged();
        mic.SelectedIndexChanged += (_, _) => SelectionChanged();
        destination.SelectedIndexChanged += (_, _) => SelectionChanged();
        files.DoubleClick += (_, _) => Play();
        files.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { if (!enterHeld) Play(); enterHeld = true; e.Handled = true; e.SuppressKeyPress = true; } };
        files.KeyUp += (_, e) => { if (e.KeyCode == Keys.Enter) enterHeld = false; };
        files.LostFocus += (_, _) => enterHeld = false;
        FormClosing += (_, _) => { timer.Stop(); Save(); StopPreview(); engine.Dispose(); };
        RefreshDevices(); RefreshFiles();
        Shown += (_, _) =>
        {
            AutoConnect();
            var unavailable = new List<string>();
            hotkeyWindow = Handle;
            for (int i = 0; i < 5; i++)
                if (GlobalHotkeys.Register(hotkeyWindow, i)) registeredHotkeys.Add(i);
                else unavailable.Add(GlobalHotkeys.Name(i));
            if (unavailable.Count > 0) status.Text = "Заняты другими программами: " + string.Join(", ", unavailable);
        };
        timer.Tick += (_, _) =>
        {
            RefreshFiles();
            microphoneLevel.Text = !connected ? "Голос: не подключён" : !voice.Checked ? "Голос: выкл." : $"Голос: {Math.Min(100, engine.MicrophonePeak * 100):F1}%";
            if (engine.Error is { } error) { Disconnect(); status.Text = "Ошибка аудио: " + error; }
            else if (wasPlaying && !engine.Playing) { wasPlaying = false; status.Text = "Звук закончился. Голос продолжает работать."; }
        };
        timer.Start();

        void Add(Control c, int column, int row, int span = 1) { layout.Controls.Add(c, column, row); layout.SetColumnSpan(c, span); }
    }

    static void ButtonInto(Control container, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 29 };
        button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(e.Message, "Soundpad", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        container.Controls.Add(button);
    }
    void RefreshDevices()
    {
        string micId = (mic.SelectedItem as DeviceItem)?.Id ?? settings.Microphone;
        string outputId = (destination.SelectedItem as DeviceItem)?.Id ?? settings.Output;
        Disconnect();
        refreshingDevices = true;
        using var enumerator = new MMDeviceEnumerator();
        var microphones = Enumerate(enumerator, DataFlow.Capture);
        var outputs = Enumerate(enumerator, DataFlow.Render).Where(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)).ToArray();
        string defaultMic = "";
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); defaultMic = device.ID; } catch { }
        mic.Items.Clear(); mic.Items.AddRange(microphones);
        destination.Items.Clear(); destination.Items.AddRange(outputs);
        mic.SelectedItem = microphones.FirstOrDefault(d => d.Id == micId)
            ?? microphones.FirstOrDefault(d => d.Id == defaultMic && !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            ?? microphones.FirstOrDefault(d => !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
        destination.SelectedItem = outputs.FirstOrDefault(d => d.Id == outputId) ?? outputs.FirstOrDefault();
        status.Text = outputs.Length == 0 ? "Нужен VB-CABLE: запусти «Установить VB-CABLE.cmd» рядом с программой." : "Готово. Выбери звук и нажми «Играть в микрофон».";
        refreshingDevices = false;
        if (IsHandleCreated) AutoConnect();
    }
    void SelectionChanged()
    {
        if (refreshingDevices) return;
        Disconnect(); Save();
        if (IsHandleCreated) AutoConnect();
    }
    void AutoConnect()
    {
        if (destination.SelectedItem is not DeviceItem) return;
        try { Connect(); }
        catch (Exception e) { status.Text = "Не подключено: " + e.Message; }
    }
    internal static DeviceItem[] Enumerate(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var result = new List<DeviceItem>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        { using (device) result.Add(new(device.ID, device.FriendlyName)); }
        return result.ToArray();
    }
    void Connect()
    {
        if (destination.SelectedItem is not DeviceItem target) throw new InvalidOperationException("Установи VB-CABLE и нажми «Обновить устройства». Затем выбери CABLE Output как микрофон в Discord / игре.");
        if (voice.Checked && mic.SelectedItem is not DeviceItem) throw new InvalidOperationException("Выбери свой микрофон.");
        engine.Start((mic.SelectedItem as DeviceItem)?.Id ?? "", target.Id, voice.Checked);
        engine.Voice = voice.Checked;
        connected = true;
        status.Text = voice.Checked ? "Подключено: твой голос + звуки → CABLE Output." : "Подключено: только звуки → CABLE Output.";
    }
    void Disconnect() { connected = false; wasPlaying = false; engine.Dispose(); }
    void Play()
    {
        if (files.SelectedItem is not string name) return;
        PlayFile(name);
    }
    void PlayFile(string name)
    {
        if (Path.GetFileName(name) != name || !File.Exists(Path.Combine(soundFolder, name)))
            throw new FileNotFoundException("Файл не найден в sounds: " + name);
        StopPreview();
        if (!connected) Connect();
        engine.Play(Path.Combine(soundFolder, name));
        nowPlaying = name; wasPlaying = true;
        status.Text = "Играет один раз: " + nowPlaying;
        try { StartPreview(name, false); }
        catch (Exception e) { status.Text = "В микрофон играет; в наушники не удалось: " + e.Message; }
    }
    void Preview()
    {
        if (files.SelectedItem is not string name) return;
        engine.Stop(); wasPlaying = false;
        StartPreview(name, true);
    }
    void StartPreview(string name, bool onlyLocal)
    {
        StopPreview();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Для прослушивания выбери наушники устройством вывода Windows.");
            previewReader = new AudioFileReader(Path.Combine(soundFolder, name)) { Volume = volume.Value / 100f };
            preview = new WasapiOut(device, AudioClientShareMode.Shared, false, 80);
            var player = preview;
            player.PlaybackStopped += (_, e) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    if (!ReferenceEquals(preview, player)) return;
                    StopPreview();
                    if (e.Exception != null) status.Text = "Ошибка прослушивания: " + e.Exception.Message;
                    else if (onlyLocal) status.Text = "Прослушивание закончено.";
                });
            };
            preview.Init(previewReader); preview.Play();
            if (onlyLocal) status.Text = "Только у себя, один раз: " + name;
        }
        catch { StopPreview(); throw; }
    }
    void StopPreview()
    {
        var player = preview; preview = null;
        player?.Dispose(); previewReader?.Dispose(); previewReader = null;
    }
    void UpdateVolume()
    {
        volumeLabel.Text = $"Громкость: {volume.Value}%";
        engine.Volume = volume.Value / 100f;
        if (previewReader != null) previewReader.Volume = engine.Volume;
    }
    void RefreshFiles(bool force = false)
    {
        try
        {
            var names = Directory.EnumerateFiles(soundFolder).Where(p => extensions.Contains(Path.GetExtension(p))).Select(Path.GetFileName).Where(n => category.SelectedIndex != 1 || IsFavorite(n!)).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (!force && names.SequenceEqual(files.Items.Cast<string>())) return;
            var selected = files.SelectedItem;
            files.BeginUpdate(); files.Items.Clear(); files.Items.AddRange(names!); files.EndUpdate();
            if (selected != null && files.Items.Contains(selected)) files.SelectedItem = selected;
            else if (files.Items.Count > 0) files.SelectedIndex = 0;
            UpdateFavoriteButton();
        }
        catch (IOException) { }
    }
    bool IsFavorite(string name) => settings.Favorites.Contains(name, StringComparer.OrdinalIgnoreCase);
    void UpdateFavoriteButton()
    {
        favoriteButton.Enabled = files.SelectedItem != null;
        favoriteButton.Text = files.SelectedItem is string name && IsFavorite(name) ? "Убрать из избранного" : "★ В избранное";
    }
    void ToggleFavorite()
    {
        if (files.SelectedItem is not string name) return;
        if (IsFavorite(name)) settings.Favorites.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        else settings.Favorites.Add(name);
        Save(); RefreshFiles(true);
    }
    void AssignHotkey()
    {
        if (files.SelectedItem is not string name) return;
        int slot = hotkeySlot.SelectedIndex;
        for (int i = 0; i < 5; i++)
            if (string.Equals(settings.Hotkeys[i], name, StringComparison.OrdinalIgnoreCase)) settings.Hotkeys[i] = null;
        settings.Hotkeys[slot] = name;
        Save(); RefreshFiles(true);
        status.Text = $"{GlobalHotkeys.Name(slot)} → {name}";
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == GlobalHotkeys.MessageId)
        {
            int slot = (int)m.WParam - GlobalHotkeys.FirstId;
            if (slot >= 0 && slot < 5 && settings.Hotkeys[slot] is string name)
            {
                try { PlayFile(name); }
                catch (Exception e) { status.Text = "Хоткей: " + e.Message; }
            }
            return;
        }
        base.WndProc(ref m);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (int slot in registeredHotkeys) GlobalHotkeys.UnregisterHotKey(hotkeyWindow, GlobalHotkeys.FirstId + slot);
        registeredHotkeys.Clear();
        hotkeyWindow = IntPtr.Zero;
        base.OnHandleDestroyed(e);
    }
    internal static void CheckLibrary()
    {
        var lines = new List<string>();
        string home = Path.Combine(Path.GetTempPath(), "soundpad-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "sounds"));
        File.WriteAllBytes(Path.Combine(home, "sounds", "first.wav"), []);
        File.WriteAllBytes(Path.Combine(home, "sounds", "second.mp4"), []);
        try
        {
            using (var form = new PadForm(home))
            {
                form.timer.Stop();
                form.files.SelectedItem = "second.mp4";
                form.ToggleFavorite();
                form.category.SelectedIndex = 1;
                if (form.files.Items.Count != 1 || (string)form.files.Items[0] != "second.mp4") throw new Exception("Favorites filter failed");
                form.hotkeySlot.SelectedIndex = 4;
                form.AssignHotkey();
            }
            using (var form = new PadForm(home))
            {
                form.timer.Stop();
                form.category.SelectedIndex = 1;
                if (form.files.Items.Count != 1 || form.settings.Hotkeys[4] != "second.mp4") throw new Exception("Favorites/hotkeys did not survive reload");
                form.hotkeySlot.SelectedIndex = 0;
                form.AssignHotkey();
                if (form.settings.Hotkeys[4] != null || form.settings.Hotkeys[0] != "second.mp4") throw new Exception("Reassignment failed");
                form.ToggleFavorite();
                if (form.files.Items.Count != 0) throw new Exception("Removing favorite failed");
            }
            lines.Add("FAVORITES FILTER / SAVE / RELOAD / REMOVE OK");
            lines.Add("HOTKEY ASSIGN / SAVE / RELOAD / REASSIGN OK");
        }
        catch (Exception e) { lines.Add("FAILED: " + e); }
        File.WriteAllLines(Path.Combine(Program.Home, "library-check.txt"), lines);
    }
    void Save()
    {
        settings.Microphone = (mic.SelectedItem as DeviceItem)?.Id ?? settings.Microphone;
        settings.Output = (destination.SelectedItem as DeviceItem)?.Id ?? settings.Output;
        settings.Volume = volume.Value; settings.Voice = voice.Checked;
        try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

static class Diagnostics
{
    public static void AudioTest()
    {
        var lines = new List<string>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            var input = PadForm.Enumerate(devices, DataFlow.Capture).First(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
            var output = PadForm.Enumerate(devices, DataFlow.Render).First(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            using var captureDevice = devices.GetDevice(input.Id);
            using var capture = new WasapiCapture(captureDevice);
            var received = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(8) };
            capture.DataAvailable += (_, e) => received.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capture.StartRecording();
            using var engine = new AudioEngine();
            engine.Start("", output.Id, false);
            engine.Play(Path.Combine(Program.Home, "sounds", "Тест.mp4"));
            Thread.Sleep(2600);
            capture.StopRecording();
            var samples = received.ToSampleProvider();
            var buffer = new float[received.BufferedBytes / (capture.WaveFormat.BitsPerSample / 8)];
            int count = samples.Read(buffer, 0, buffer.Length);
            float peak = buffer.Take(count).Max(v => Math.Abs(v));
            if (peak < .005f) throw new Exception("No audio reached CABLE Output");
            lines.Add($"MP4 -> CABLE Input -> CABLE Output OK: peak={peak:F5}, received={count} samples");
            var mp3 = Directory.EnumerateFiles(Path.Combine(Program.Home, "sounds"), "*.mp3").FirstOrDefault();
            if (mp3 != null)
            {
                using var fileInfo = new AudioFileReader(mp3);
                if (fileInfo.TotalTime.TotalSeconds < 7)
                {
                    received.ClearBuffer();
                    capture.StartRecording();
                    engine.Play(mp3);
                    Thread.Sleep((int)fileInfo.TotalTime.TotalMilliseconds + 450);
                    capture.StopRecording();
                    if (engine.Playing) throw new Exception("MP3 did not stop after end");
                    buffer = new float[received.BufferedBytes / (capture.WaveFormat.BitsPerSample / 8)];
                    count = received.ToSampleProvider().Read(buffer, 0, buffer.Length);
                    peak = buffer.Take(count).Max(v => Math.Abs(v));
                    if (peak < .005f) throw new Exception("MP3 did not reach CABLE Output");
                    lines.Add($"MP3 -> CABLE Output / AUTO STOP OK: {Path.GetFileName(mp3)}, peak={peak:F5}");
                }
            }
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Program.Home, "settings.json"))) ?? new();
            using var selectedMic = string.IsNullOrEmpty(settings.Microphone)
                ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : devices.GetDevice(settings.Microphone);
            foreach (var id in new[] { selectedMic.ID, devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID }.Distinct())
            {
                using var micDevice = devices.GetDevice(id);
                engine.Start(id, output.Id, true);
                engine.Play(Path.Combine(Program.Home, "sounds", "Тест.mp4"));
                Thread.Sleep(700);
                if (engine.Error != null) throw new Exception(engine.Error);
                lines.Add("MICROPHONE + MP4 MIX PIPELINE OK: " + micDevice.FriendlyName);
            }
        }
        catch (Exception e) { lines.Add("AUDIO TEST FAILED: " + e); }
        File.WriteAllLines(Path.Combine(Program.Home, "audio-test.txt"), lines);
    }

    public static void Run()
    {
        var lines = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var role in new[] { Role.Multimedia, Role.Communications })
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                    lines.Add($"DEFAULT {flow}/{role}: {device.FriendlyName}");
                }
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var d in PadForm.Enumerate(enumerator, flow))
                {
                    using var device = enumerator.GetDevice(d.Id);
                    lines.Add($"{flow}: {d.Name} | {d.Id} | mute={device.AudioEndpointVolume.Mute}, volume={device.AudioEndpointVolume.MasterVolumeLevelScalar:F2}");
                }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Program.Home, "sounds")))
            {
                using var reader = new AudioFileReader(file);
                var buffer = new float[48000];
                int count = reader.Read(buffer, 0, buffer.Length);
                lines.Add($"DECODE OK: {Path.GetFileName(file)} | {reader.WaveFormat} | {reader.TotalTime} | {count} samples");
                if (count == 0) throw new InvalidOperationException("Empty audio track");
                using var engine = new AudioEngine { Volume = 1 };
                var mixed = new float[96000];
                engine.Play(file); engine.Read(mixed, 0, mixed.Length);
                float full = mixed.Max(v => Math.Abs(v));
                engine.Volume = .25f;
                engine.Play(file); engine.Read(mixed, 0, mixed.Length);
                float quarter = mixed.Max(v => Math.Abs(v));
                if (full > .00001 && Math.Abs(quarter / full - .25f) > .001f) throw new Exception("Volume test failed");
                engine.Stop(); engine.Read(mixed, 0, mixed.Length);
                if (mixed.Any(v => v != 0)) throw new Exception("Stop test failed");
                lines.Add($"MIX/VOLUME/STOP OK: peak={full:F5}; 25%={quarter:F5}");
                engine.Play(file);
                int blocks = (int)Math.Ceiling(reader.TotalTime.TotalSeconds) + 2;
                for (int block = 0; block < blocks; block++) engine.Read(mixed, 0, mixed.Length);
                if (engine.Playing) throw new Exception("One-shot completion failed");
                engine.Read(mixed, 0, mixed.Length);
                if (mixed.Any(v => v != 0)) throw new Exception("Audio repeated after end");
                lines.Add("ONE-SHOT / SILENCE AFTER END OK: " + Path.GetFileName(file));
            }
            lines.Add("CHECK OK");
        }
        catch (Exception ex) { lines.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(Program.Home, "diagnostics.txt"), lines);
    }
}
