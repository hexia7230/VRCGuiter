using NAudio.CoreAudioApi;
using VRCGuiter.Audio;
using VRCGuiter.VirtualMic;

namespace VRCGuiter;

public sealed class MainForm : Form
{
    private sealed class DeviceItem
    {
        public string Id = "";
        public string Name = "";
        public MMDevice? Device;
        public CablePair? Cable;
        public override string ToString() => Name;
    }

    private readonly MMDeviceEnumerator _enum = new();
    private readonly AudioEngine _engine = new();
    private readonly AppSettings _s = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };

    private readonly TableLayoutPanel _grid = new();
    private int _row;
    private readonly Label _lblCable = new();
    private readonly Label _lblVrcMic = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblNoiseState = new();
    private readonly Button _btnSetup = new();
    private readonly Button _btnLearn = new();
    private readonly Button _btnMonitor = new();
    private readonly Button _btnStart = new();
    private readonly ComboBox _cmbInput = new();
    private readonly ComboBox _cmbOutput = new();
    private readonly ComboBox _cmbMonitor = new();
    private readonly CheckBox _chkExclusive = new();
    private readonly ProgressBar _pbIn = new();
    private readonly ProgressBar _pbOut = new();

    private List<CablePair> _cables = new();
    private bool _loading = true;
    private bool _refreshing;
    private bool _monitorOn;
    private bool _learning;
    private bool _setupRunning;

    private bool Busy => _loading || _refreshing;

    public MainForm()
    {
        Text = "VRCGuiter";
        Font = new Font("Yu Gothic UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _grid.AutoSize = true;
        _grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _grid.MinimumSize = new Size(560, 0);
        _grid.MaximumSize = new Size(560, 0);
        _grid.ColumnCount = 3;
        _grid.Padding = new Padding(12, 6, 12, 12);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        Controls.Add(_grid);

        BuildUi();
    }

    // ---------------------------------------------------------------- UI 構築

    private void BuildUi()
    {
        Heading("仮想マイク");
        _lblCable.AutoSize = false;
        _lblCable.Height = 22;
        _lblCable.TextAlign = ContentAlignment.MiddleLeft;
        _lblCable.Margin = new Padding(0, 4, 4, 0);
        _btnSetup.Text = "VRCG 作成";
        _btnSetup.AutoSize = true;
        _btnSetup.Click += (_, _) => RunSetup();
        _lblCable.AutoEllipsis = true;
        Row("状態", _lblCable, _btnSetup);
        _lblVrcMic.AutoSize = true;
        _lblVrcMic.Font = new Font(Font, FontStyle.Bold);
        _lblVrcMic.Margin = new Padding(0, 6, 0, 0);
        Row("VRChat のマイク", _lblVrcMic);

        Heading("入出力");
        SetupCombo(_cmbInput);
        Row("入力マイク", _cmbInput);
        _chkExclusive.Text = "排他モード（Windows の音声処理を迂回）";
        _chkExclusive.AutoSize = true;
        _chkExclusive.Checked = _s.Exclusive;
        Row("", _chkExclusive);
        SetupCombo(_cmbOutput);
        Row("出力先", _cmbOutput);
        _pbIn.Height = 12; _pbIn.Margin = new Padding(0, 8, 0, 0);
        _pbOut.Height = 12; _pbOut.Margin = new Padding(0, 8, 0, 0);
        Row("入力レベル", _pbIn);
        Row("出力レベル", _pbOut);

        Heading("ノイズ除去");
        _btnLearn.Text = "ノイズ学習（2秒 静かに）";
        _btnLearn.AutoSize = true;
        _btnLearn.Click += (_, _) => StartLearning();
        _lblNoiseState.AutoSize = true;
        _lblNoiseState.Margin = new Padding(8, 7, 0, 0);
        Row("学習", Flow(_btnLearn, _lblNoiseState));
        Slider("強さ", 0, 100, _s.NoiseStrength, v => v + "%", v => { _engine.NoiseReducer.Strength = v / 100f; _s.NoiseStrength = v; });
        Slider("ゲート", -100, -30, _s.GateThresholdDb, v => v <= -100 ? "オフ" : v + " dB", v => { _engine.Gate.ThresholdDb = v; _s.GateThresholdDb = v; });

        Heading("リバーブ");
        Slider("量", 0, 100, _s.ReverbWet, v => v + "%", v => { _engine.Reverb.Wet = v / 100f; _s.ReverbWet = v; });
        Slider("広さ", 0, 100, _s.ReverbRoom, v => v + "%", v => { _engine.Reverb.Room = v / 100f; _s.ReverbRoom = v; });
        Slider("明るさ", 0, 100, 100 - _s.ReverbDamp, v => v + "%", v => { _engine.Reverb.Damp = (100 - v) / 100f; _s.ReverbDamp = 100 - v; });

        Heading("出力");
        Slider("音量", -24, 24, _s.OutputGainDb, v => (v > 0 ? "+" : "") + v + " dB", v => { _engine.OutputGain = (float)Math.Pow(10, v / 20.0); _s.OutputGainDb = v; });

        Heading("モニター");
        SetupCombo(_cmbMonitor);
        _cmbMonitor.Width = 250;
        _btnMonitor.Text = "開始";
        _btnMonitor.AutoSize = true;
        _btnMonitor.Click += (_, _) => { _monitorOn = !_monitorOn; ApplyMonitor(); };
        Row("ヘッドホン", Flow(_cmbMonitor, _btnMonitor));

        _btnStart.Text = "開始";
        _btnStart.Size = new Size(110, 32);
        _btnStart.Margin = new Padding(0, 12, 10, 0);
        _btnStart.Click += (_, _) => { if (_engine.Running) StopEngine(); else TryStart(true); };
        _lblStatus.AutoSize = true;
        _lblStatus.Margin = new Padding(0, 20, 0, 0);
        var bottom = Flow(_btnStart, _lblStatus);
        _grid.Controls.Add(bottom, 0, _row);
        _grid.SetColumnSpan(bottom, 3);
        _row++;
    }

    private void Heading(string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };
        _grid.Controls.Add(l, 0, _row);
        _grid.SetColumnSpan(l, 3);
        _row++;
    }

    private void Row(string label, Control c, Control? right = null)
    {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 4, 0) };
        _grid.Controls.Add(l, 0, _row);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _grid.Controls.Add(c, 1, _row);
        if (right != null)
        {
            right.Anchor = AnchorStyles.Left;
            _grid.Controls.Add(right, 2, _row);
        }
        else _grid.SetColumnSpan(c, 2);
        _row++;
    }

    private void Slider(string label, int min, int max, int value, Func<int, string> fmt, Action<int> onChange)
    {
        var tb = new TrackBar
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            TickStyle = TickStyle.None, AutoSize = false, Height = 26, Margin = new Padding(0, 2, 0, 0),
        };
        var vl = new Label { AutoSize = true, Text = fmt(tb.Value), Margin = new Padding(0, 6, 0, 0) };
        tb.ValueChanged += (_, _) => { vl.Text = fmt(tb.Value); onChange(tb.Value); if (!_loading) ScheduleSave(); };
        onChange(tb.Value);
        Row(label, tb, vl);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in controls) p.Controls.Add(c);
        return p;
    }

    private static void SetupCombo(ComboBox cmb)
    {
        cmb.DropDownStyle = ComboBoxStyle.DropDownList;
        cmb.Margin = new Padding(0, 3, 0, 0);
    }

    // ---------------------------------------------------------------- 起動と終了

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _engine.Stopped += ex => BeginInvoke(() => OnEngineStopped(ex));
        _engine.NoiseLearned += p => BeginInvoke(() => OnNoiseLearned(p));
        _uiTimer.Tick += (_, _) => UpdateMeters();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _s.Save(); };

        _cmbInput.DropDown += (_, _) => RefreshDevices();
        _cmbOutput.DropDown += (_, _) => RefreshDevices();
        _cmbMonitor.DropDown += (_, _) => RefreshDevices();
        _cmbInput.SelectedIndexChanged += (_, _) => { if (Busy) return; _s.InputDeviceId = SelectedInput()?.Id; ScheduleSave(); if (_engine.Running) TryStart(false); };
        _cmbOutput.SelectedIndexChanged += (_, _) => { if (Busy) return; _s.OutputDeviceId = SelectedOutput()?.Id; ScheduleSave(); UpdateCableLabels(); if (_engine.Running) TryStart(false); };
        _cmbMonitor.SelectedIndexChanged += (_, _) => { if (Busy) return; _s.MonitorDeviceId = SelectedMonitor()?.Id; ScheduleSave(); if (_monitorOn) ApplyMonitor(); };
        _chkExclusive.CheckedChanged += (_, _) => { if (Busy) return; _s.Exclusive = _chkExclusive.Checked; ScheduleSave(); if (_engine.Running) TryStart(false); };

        RefreshDevices();
        _loading = false;
        _uiTimer.Start();

        bool hasVrcg = _cables.Any(c => c.IsVbCable);
        if (!hasVrcg && !_s.SetupDeclined)
        {
            var r = MessageBox.Show(this,
                "仮想マイク「VRCG」がまだありません（初回のみの準備です）。\n\n" +
                "無料の仮想オーディオドライバ VB-CABLE（vb-audio.com）を自動でダウンロードして導入し、\n" +
                "「VRCG」という名前のマイクを作ります。\n" +
                "途中で管理者権限の確認と、Windows のドライバ確認（「インストール」を押す）が出ます。\n\n" +
                "今すぐ作成しますか？",
                "VRCGuiter", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) { RunSetup(); return; }
            _s.SetupDeclined = true;
            ScheduleSave();
        }
        if (SelectedOutput()?.Cable != null) TryStart(false);
        else SetStatus("出力先に仮想ケーブルがありません。「VRCG を作成」を押してください。", true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _engine.Stop();
        _engine.Dispose();
        _s.Save();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- デバイス

    private DeviceItem? SelectedInput() => _cmbInput.SelectedItem as DeviceItem;
    private DeviceItem? SelectedOutput() => _cmbOutput.SelectedItem as DeviceItem;
    private DeviceItem? SelectedMonitor() => _cmbMonitor.SelectedItem as DeviceItem;

    private void RefreshDevices()
    {
        _refreshing = true;
        try
        {
            var renders = VirtualCable.Snapshot(_enum, DataFlow.Render);
            var captures = VirtualCable.Snapshot(_enum, DataFlow.Capture);
            _cables = VirtualCable.Detect(renders, captures);
            var cableCaptureIds = _cables.Select(c => c.Capture.Id).ToHashSet();
            var cableRenderIds = _cables.Select(c => c.Render.Id).ToHashSet();

            // 入力: 仮想ケーブルの録音側は除外（ループ防止）。既定は仮想っぽくない物理マイク
            var inputs = captures.Where(d => !cableCaptureIds.Contains(d.Id))
                .Select(d => new DeviceItem { Id = d.Id, Name = d.Name, Device = d.Device }).ToList();
            string? defIn = DefaultId(DataFlow.Capture, Role.Communications);
            if (defIn == null || captures.Any(d => d.Id == defIn && VirtualCable.LooksVirtual(d.Adapter)))
                defIn = captures.FirstOrDefault(d => !cableCaptureIds.Contains(d.Id) && !VirtualCable.LooksVirtual(d.Adapter))?.Id ?? defIn;
            Fill(_cmbInput, inputs, _s.InputDeviceId ?? defIn);

            var outputs = new List<DeviceItem>();
            foreach (var c in _cables)
                outputs.Add(new DeviceItem { Id = c.Render.Id, Name = c.Label + "  →  " + c.Render.Name, Device = c.Render.Device, Cable = c });
            foreach (var d in renders)
                if (!cableRenderIds.Contains(d.Id))
                    outputs.Add(new DeviceItem { Id = d.Id, Name = d.Name, Device = d.Device });
            string? preferredOut = _s.OutputDeviceId;
            if (preferredOut == null || outputs.All(o => o.Id != preferredOut || o.Cable == null))
                preferredOut = _cables.FirstOrDefault()?.Render.Id ?? preferredOut;
            Fill(_cmbOutput, outputs, preferredOut);

            var monitors = renders.Where(d => !cableRenderIds.Contains(d.Id))
                .Select(d => new DeviceItem { Id = d.Id, Name = d.Name, Device = d.Device }).ToList();
            Fill(_cmbMonitor, monitors, _s.MonitorDeviceId ?? DefaultId(DataFlow.Render, Role.Multimedia));

            UpdateCableLabels();
        }
        catch (Exception ex)
        {
            SetStatus("デバイス一覧の取得に失敗: " + ex.Message, true);
        }
        finally { _refreshing = false; }
    }

    private string? DefaultId(DataFlow flow, Role role)
    {
        try
        {
            if (_enum.HasDefaultAudioEndpoint(flow, role))
                return _enum.GetDefaultAudioEndpoint(flow, role).ID;
        }
        catch { }
        return null;
    }

    private static void Fill(ComboBox cmb, List<DeviceItem> items, string? selectId)
    {
        string? current = (cmb.SelectedItem as DeviceItem)?.Id ?? selectId;
        cmb.BeginUpdate();
        cmb.Items.Clear();
        foreach (var i in items) cmb.Items.Add(i);
        int idx = items.FindIndex(i => i.Id == current);
        if (idx < 0 && items.Count > 0) idx = 0;
        cmb.SelectedIndex = idx;
        cmb.EndUpdate();
    }

    private void UpdateCableLabels()
    {
        var o = SelectedOutput();
        if (o?.Cable != null)
        {
            bool isVrcg = o.Cable.IsVbCable && o.Cable.MicName.StartsWith(VirtualCable.VrcgName, StringComparison.OrdinalIgnoreCase);
            _lblCable.Text = isVrcg ? "VRCG 利用可能" : (o.Cable.IsVbCable ? "VB-CABLE 検出（未改名）" : "仮想ケーブル検出: " + o.Cable.Label);
            _lblVrcMic.Text = o.Cable.MicName;
            _btnSetup.Visible = !isVrcg;
            _btnSetup.Text = o.Cable.IsVbCable ? "VRCG 改名" : "VRCG 作成";
        }
        else
        {
            _lblCable.Text = "仮想マイクがありません";
            _lblVrcMic.Text = "（出力先に対応するマイクを VRChat で選んでください）";
            _btnSetup.Visible = true;
            _btnSetup.Text = "VRCG 作成";
        }
    }

    // ---------------------------------------------------------------- エンジン

    private void TryStart(bool interactive)
    {
        var inp = SelectedInput();
        var outp = SelectedOutput();
        if (inp?.Device == null || outp?.Device == null)
        {
            SetStatus("入力マイクと出力先を選んでください", true);
            return;
        }
        if (outp.Cable == null)
        {
            if (!interactive) { SetStatus("出力先に仮想ケーブルを選んでください", true); return; }
            var r = MessageBox.Show(this,
                "出力先が仮想ケーブルではありません。スピーカーを選ぶとハウリングすることがあります。\nこのまま開始しますか？",
                "VRCGuiter", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }
        try
        {
            _engine.Start(inp.Device, outp.Device, _chkExclusive.Checked, _monitorOn ? SelectedMonitor()?.Device : null);
            LoadNoiseProfile();
            _btnStart.Text = "停止";
            SetStatus($"動作中  {_engine.SampleRate / 1000.0:0.#} kHz{(_engine.Exclusive ? "（排他）" : "")}  遅延 約 {_engine.LatencyMsEstimate} ms", false);
        }
        catch (Exception ex)
        {
            _engine.Stop();
            _btnStart.Text = "開始";
            SetStatus("開始できません: " + ex.Message, true);
        }
    }

    private void StopEngine()
    {
        _engine.Stop();
        _btnStart.Text = "開始";
        SetStatus("停止中", false);
    }

    private void OnEngineStopped(Exception? ex)
    {
        _btnStart.Text = "開始";
        _learning = false;
        _btnLearn.Enabled = true;
        _btnLearn.Text = "ノイズ学習（2秒 静かに）";
        SetStatus(ex != null ? "停止しました（デバイスの問題）: " + ex.Message : "停止中", ex != null);
    }

    private string NoiseKey() => (SelectedInput()?.Id ?? "") + "|" + _engine.SampleRate;

    private void LoadNoiseProfile()
    {
        if (_s.NoiseProfiles.TryGetValue(NoiseKey(), out var p))
        {
            _engine.NoiseReducer.SetProfile(p);
            _lblNoiseState.Text = "学習済み";
        }
        else
        {
            _engine.NoiseReducer.SetProfile(null);
            _lblNoiseState.Text = "未学習";
        }
    }

    private void StartLearning()
    {
        if (!_engine.Running || _learning) return;
        _learning = true;
        _btnLearn.Enabled = false;
        _btnLearn.Text = "学習中…";
        _engine.StartNoiseLearning(2.0);
    }

    private void OnNoiseLearned(float[] profile)
    {
        _s.NoiseProfiles[NoiseKey()] = profile;
        ScheduleSave();
        _learning = false;
        _btnLearn.Enabled = true;
        _btnLearn.Text = "ノイズ学習（2秒 静かに）";
        _lblNoiseState.Text = "学習済み";
    }

    private void ApplyMonitor()
    {
        try
        {
            _engine.SetMonitor(_monitorOn ? SelectedMonitor()?.Device : null);
            _btnMonitor.Text = _monitorOn ? "停止" : "開始";
        }
        catch (Exception ex)
        {
            _monitorOn = false;
            _btnMonitor.Text = "開始";
            SetStatus("モニター出力に失敗: " + ex.Message, true);
        }
    }

    // ---------------------------------------------------------------- セットアップ

    private async void RunSetup()
    {
        if (_setupRunning) return;
        _setupRunning = true;
        _btnSetup.Enabled = false;
        try
        {
            var progress = new Progress<string>(t => _lblCable.Text = t);
            var result = await VbCableSetup.RunAsync(progress, CancellationToken.None);
            RefreshDevices();
            var vb = _cables.FirstOrDefault(c => c.IsVbCable);
            if (vb != null)
            {
                _refreshing = true;
                for (int i = 0; i < _cmbOutput.Items.Count; i++)
                    if (_cmbOutput.Items[i] is DeviceItem di && di.Id == vb.Render.Id) { _cmbOutput.SelectedIndex = i; break; }
                _refreshing = false;
                _s.OutputDeviceId = vb.Render.Id;
                ScheduleSave();
            }
            UpdateCableLabels();
            if (result.Ok)
            {
                _s.SetupDeclined = false;
                TryStart(false);
            }
            else
            {
                MessageBox.Show(this, result.Message, "VRCG セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (SelectedOutput()?.Cable != null && !_engine.Running) TryStart(false);
            }
        }
        finally
        {
            _setupRunning = false;
            _btnSetup.Enabled = true;
        }
    }

    // ---------------------------------------------------------------- 雑多

    private void UpdateMeters()
    {
        SetMeter(_pbIn, _engine.ReadInputPeak());
        SetMeter(_pbOut, _engine.ReadOutputPeak());
    }

    private static void SetMeter(ProgressBar pb, float peak)
    {
        int v = 0;
        if (peak > 1e-5f)
        {
            double db = 20 * Math.Log10(peak);
            v = (int)Math.Clamp((db + 60) / 60 * 100, 0, 100);
        }
        // Windows のプログレスバーは増加時にアニメーションして遅れるので、一度上に振ってから戻す
        if (v < 100) { pb.Value = v + 1; pb.Value = v; }
        else pb.Value = 100;
    }

    private void SetStatus(string text, bool error)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
