using System.Diagnostics;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Gui;

public sealed class MainForm : Form
{
    private const string AutoClusterText = "自動（推奨: Windows 既定。32GB 超は 32 KB）";

    private readonly ComboBox _cmbDrive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _btnRefresh = new() { Text = "更新(&R)", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly CheckBox _chkShowFixed = new() { Text = "固定ディスクとして認識されるドライブも表示する（危険：内蔵ディスクが混ざります）", AutoSize = true };
    private readonly ComboBox _cmbCluster = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _txtLabel = new() { Dock = DockStyle.Fill, MaxLength = 11 };
    private readonly CheckBox _chkQuick = new() { Text = "クイックフォーマット（システム領域のみ初期化。オフにすると全領域を書き込み検査し不良クラスタを登録）", Checked = true, AutoSize = true };
    private readonly CheckBox _chkPtype = new() { Text = "MBR のパーティション種別を FAT32 (0x0C) に修正する（機器の互換性向上）", Checked = true, AutoSize = true };
    private readonly TextBox _txtInfo = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = MonoFont(), WordWrap = false };
    private readonly Button _btnFormat = new() { Text = "フォーマット開始(&F)", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
    private readonly Button _btnCancel = new() { Text = "中止", AutoSize = true, Enabled = false };
    private readonly Button _btnDiagnostics = new() { Text = "検査(&T)...", AutoSize = true, Margin = new Padding(24, 0, 0, 0) };
    private readonly Button _btnData = new() { Text = "データ管理(&M)...", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000 };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "準備完了" };
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = MonoFont() };

    private List<DriveEntry> _drives = new();
    private List<int> _clusterChoices = new(); // 0 = 自動
    private CancellationTokenSource? _cts;
    private readonly char? _initialLetter;
    private int _requestedCluster;
    private bool _suppressEvents;
    /// <summary>抜き差しの通知はまとめて届くので、少し待ってから 1 回だけ一覧を更新する。</summary>
    private readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 800 };

    /// <summary>日本語も桁がそろう等幅フォント（Consolas だと日本語が別フォントで描かれ、表の「:」がずれる）。</summary>
    private static Font MonoFont()
    {
        foreach (string name in new[] { "BIZ UDGothic", "MS Gothic" })
        {
            var f = new Font(name, 9.5F);
            if (string.Equals(f.FontFamily.Name, name, StringComparison.OrdinalIgnoreCase) || f.FontFamily.GetName(1033) == name) return f;
            f.Dispose();
        }
        return new Font(FontFamily.GenericMonospace, 9F);
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && (int)m.WParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        base.WndProc(ref m);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cts == null) return;
        // フォーマット中に終了するとボリュームがロックされたまま中途半端な状態で残るため止める
        e.Cancel = true;
        MessageBox.Show(this, "フォーマット中は終了できません。「中止」を押して処理が止まってから閉じてください。",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public MainForm(char? initialLetter, string? initialLabel, int initialCluster)
    {
        _initialLetter = initialLetter;
        _requestedCluster = initialCluster;
        Text = "FAT32FMT - 大容量 SD カード FAT32 フォーマッタ";
        Font = new Font("Yu Gothic UI", 9F);
        MinimumSize = new Size(760, 720);
        Size = new Size(860, 780);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        RebuildClusterChoices(null);
        if (initialLabel != null) _txtLabel.Text = initialLabel;

        _btnRefresh.Click += (_, _) => RefreshDrives();
        _chkShowFixed.CheckedChanged += (_, _) => RefreshDrives();
        _cmbDrive.SelectedIndexChanged += (_, _) => { if (!_suppressEvents) OnDriveChanged(); };
        _cmbCluster.SelectedIndexChanged += (_, _) => { if (!_suppressEvents) { _requestedCluster = SelectedClusterSize; UpdateInfo(); } };
        _txtLabel.TextChanged += (_, _) => UpdateInfo();
        _btnFormat.Click += async (_, _) => await StartFormatAsync();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _btnDiagnostics.Click += (_, _) => OpenDiagnostics();
        _btnData.Click += (_, _) => OpenDataManager();
        _deviceChangeTimer.Tick += (_, _) => { _deviceChangeTimer.Stop(); if (_cts == null) RefreshDrives(); };
        FormClosing += OnFormClosing;
        Load += (_, _) =>
        {
            if (!VolumeFormatService.IsElevated())
            {
                Log("警告: 管理者権限で実行されていません。フォーマットは失敗します。右クリック →「管理者として実行」で起動してください。");
                _btnFormat.Enabled = false;
            }
            RefreshDrives();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        void AddRow(string label, Control c, int height = 0)
        {
            root.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
            c.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(c, 1, row);
            row++;
        }
        void AddFill(Control c)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            c.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(c, 0, row);
            root.SetColumnSpan(c, 2);
            row++;
        }

        var drivePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        drivePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        drivePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbDrive.Margin = new Padding(0, 0, 6, 0);
        _btnRefresh.Margin = Padding.Empty;
        drivePanel.Controls.Add(_cmbDrive, 0, 0);
        drivePanel.Controls.Add(_btnRefresh, 1, 0);

        AddRow("ドライブ(&D):", drivePanel);
        AddRow("", _chkShowFixed);
        AddRow("クラスタサイズ(&C):", _cmbCluster);
        AddRow("ボリュームラベル(&L):", _txtLabel);
        AddRow("", _chkQuick);
        AddRow("", _chkPtype);
        AddRow("対象の情報:", _txtInfo, 270);

        var btnPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        _btnFormat.Margin = new Padding(0, 0, 8, 0);
        btnPanel.Controls.Add(_btnFormat);
        btnPanel.Controls.Add(_btnCancel);
        btnPanel.Controls.Add(_btnDiagnostics);
        btnPanel.Controls.Add(_btnData);
        AddRow("", btnPanel);
        AddRow("進行状況:", _progress, 28);
        AddRow("", _lblStatus);
        AddFill(_txtLog);

        Controls.Add(root);
    }

    // ---- ドライブ一覧 ---------------------------------------------------------

    private void RefreshDrives()
    {
        char? keep = SelectedDrive?.Letter ?? _initialLetter;
        try
        {
            _drives = DriveEnumerator.Enumerate(includeFixed: _chkShowFixed.Checked);
        }
        catch (Exception ex)
        {
            Log($"ドライブ一覧の取得に失敗: {ex.Message}");
            _drives = new();
        }

        _suppressEvents = true;
        _cmbDrive.Items.Clear();
        foreach (var d in _drives) _cmbDrive.Items.Add(d.Display);
        int idx = keep is char k ? _drives.FindIndex(d => d.Letter == k) : -1;
        if (idx < 0 && _drives.Count > 0) idx = 0;
        _cmbDrive.SelectedIndex = idx;
        _suppressEvents = false;

        if (_drives.Count == 0)
            _txtInfo.Text = "フォーマット可能なリムーバブルドライブが見つかりません。\r\nSD カードを挿入して「更新」を押してください。";
        OnDriveChanged();
    }

    private DriveEntry? SelectedDrive =>
        _cmbDrive.SelectedIndex >= 0 && _cmbDrive.SelectedIndex < _drives.Count ? _drives[_cmbDrive.SelectedIndex] : null;

    private int SelectedClusterSize =>
        _cmbCluster.SelectedIndex >= 0 && _cmbCluster.SelectedIndex < _clusterChoices.Count ? _clusterChoices[_cmbCluster.SelectedIndex] : 0;

    /// <summary>選択中ドライブで成立するクラスタサイズだけを選択肢にする。</summary>
    private void RebuildClusterChoices(DriveEntry? d)
    {
        var sizes = new List<int> { 0 };
        if (d is { SizeBytes: > 0 })
        {
            try { sizes.AddRange(ClusterSizePolicy.ValidSizes(d.SizeBytes, d.SectorSize)); }
            catch { sizes.AddRange(ClusterSizePolicy.Candidates); }
        }
        else sizes.AddRange(ClusterSizePolicy.Candidates);

        _suppressEvents = true;
        _clusterChoices = sizes;
        _cmbCluster.Items.Clear();
        foreach (int s in sizes) _cmbCluster.Items.Add(s == 0 ? AutoClusterText : SizeFormat.Cluster(s));
        int idx = sizes.IndexOf(_requestedCluster);
        _cmbCluster.SelectedIndex = idx < 0 ? 0 : idx;
        _suppressEvents = false;
    }

    private void OnDriveChanged()
    {
        RebuildClusterChoices(SelectedDrive);
        UpdateInfo();
    }

    private FormatRequest BuildRequest(DriveEntry d) => new()
    {
        DriveLetter = d.Letter,
        ClusterBytes = SelectedClusterSize,
        Label = _txtLabel.Text.Trim().Length > 0 ? _txtLabel.Text.Trim() : null,
        FullFormat = !_chkQuick.Checked,
        FixPartitionType = _chkPtype.Checked,
        AllowFixedDisk = d.Kind == DriveKind.Fixed,
    };

    private void UpdateInfo()
    {
        var d = SelectedDrive;
        if (d == null) { _btnFormat.Enabled = false; return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(d.DescribeDetails());
        sb.AppendLine();
        sb.AppendLine("--- フォーマット後のレイアウト（予定） ---");
        try
        {
            var plan = VolumeFormatService.Plan(BuildRequest(d));
            sb.Append(plan.Describe());
            _btnFormat.Enabled = VolumeFormatService.IsElevated() && _cts == null;
        }
        catch (Exception ex) when (ex is VolumeFormatException or Fat32LayoutException or ArgumentException)
        {
            sb.Append("エラー: ").Append(ex.Message);
            _btnFormat.Enabled = false;
        }
        _txtInfo.Text = sb.ToString();
    }

    // ---- フォーマット実行 -------------------------------------------------------

    private async Task StartFormatAsync()
    {
        var d = SelectedDrive;
        if (d == null) { MessageBox.Show(this, "ドライブを選択してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var req = BuildRequest(d);
        FormatPlan plan;
        try
        {
            plan = VolumeFormatService.Plan(req);
        }
        catch (Exception ex) when (ex is VolumeFormatException or Fat32LayoutException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "フォーマットできません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string msg =
            "次のドライブを FAT32 でフォーマットします。ドライブ上のデータはすべて消去されます。\r\n\r\n" +
            $"{d.Display}\r\n\r\n" +
            $"クラスタサイズ: {SizeFormat.Cluster(plan.Layout.BytesPerCluster)}   ラベル: {req.Label ?? "(なし)"}\r\n" +
            $"方式: {(req.FullFormat ? "通常（全領域を書き込み検査。時間がかかります）" : "クイック")}\r\n\r\n" +
            "本当に続行しますか？";
        if (MessageBox.Show(this, msg, "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        if (d.Kind == DriveKind.Fixed)
        {
            var r = MessageBox.Show(this,
                $"ドライブ {d.Letter}: は「固定ディスク」として認識されています。内蔵 HDD/SSD の可能性があります。\r\n\r\nそれでもフォーマットしますか？",
                "最終確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _txtLog.Clear();
        _progress.Value = 0;
        Log($"=== {d.Letter}: のフォーマットを開始 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");

        var progress = new Progress<OperationProgress>(p =>
        {
            _progress.Value = Math.Clamp((int)(p.Fraction * 1000), 0, 1000);
            string speed = p.BytesPerSecond > 0 ? $"  {SizeFormat.Speed(p.BytesPerSecond)}" : "";
            string eta = p.Remaining is { } rem ? $"  残り約 {rem:hh\\:mm\\:ss}" : "";
            _lblStatus.Text = p.Total > 0
                ? $"{p.Phase}  {p.Fraction * 100:0.0}%  ({SizeFormat.Binary(p.Done)} / {SizeFormat.Binary(p.Total)}){speed}{eta}"
                : p.Phase;
        });
        var ct = _cts.Token;

        try
        {
            var result = await Task.Run(() => VolumeFormatService.Format(req, progress, s => BeginInvoke(() => Log(s)), ct), ct);
            _progress.Value = 1000;
            _lblStatus.Text = "完了";
            Log($"=== 完了 ({result.Elapsed.TotalSeconds:0.0} 秒) ===");
            MessageBox.Show(this,
                $"{result.DriveLetter}: を FAT32 でフォーマットしました。\r\n\r\n" +
                $"クラスタサイズ: {SizeFormat.Cluster(result.Layout.BytesPerCluster)}\r\n" +
                $"利用可能容量: {SizeFormat.Both(result.Layout.DataBytes)}\r\n" +
                (result.BadClusters.Count > 0 ? $"不良クラスタ: {result.BadClusters.Count} 個\r\n" : "") +
                (result.MountedFileSystem != null ? $"再マウント確認: {result.MountedFileSystem}" : "再マウントは確認できませんでした。カードを抜き差ししてください。"),
                "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "中止されました";
            Log("ユーザーにより中止されました。ドライブは未フォーマット状態の可能性があります。再度フォーマットしてください。");
            MessageBox.Show(this, "フォーマットを中止しました。\r\nドライブは中途半端な状態の可能性があるため、再度フォーマットしてください。", "中止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "エラー";
            string m = ex is VolumeFormatException or Fat32LayoutException or ArgumentException or IOException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log("エラー: " + m);
            MessageBox.Show(this, m, "フォーマットに失敗しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshDrives();
        }
    }

    private void SetBusy(bool busy)
    {
        _cmbDrive.Enabled = _btnRefresh.Enabled = _chkShowFixed.Enabled = _cmbCluster.Enabled =
            _txtLabel.Enabled = _chkQuick.Enabled = _chkPtype.Enabled = !busy;
        _btnFormat.Enabled = !busy && VolumeFormatService.IsElevated();
        _btnCancel.Enabled = busy;
        _btnDiagnostics.Enabled = !busy;
        _btnData.Enabled = !busy;
        UseWaitCursor = busy;
    }

    /// <summary>バックアップ / 書き戻し / 消去 / 削除ファイル復旧のダイアログを開く。</summary>
    private void OpenDataManager()
    {
        using var dlg = new DataForm(SelectedDrive?.Letter);
        dlg.ShowDialog(this);
        RefreshDrives();
    }

    /// <summary>選択中のドライブの検査ダイアログ（速度・不良セクタ・容量偽装）を開く。</summary>
    private void OpenDiagnostics()
    {
        var d = SelectedDrive;
        if (d == null) { MessageBox.Show(this, "ドライブを選択してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dlg = new DiagnosticsDialog(d);
        dlg.ShowDialog(this);
        if (dlg.DestroyedData)
            Log($"{d.Letter}: は検査パターンで上書きされました。使う前にフォーマットしてください。");
        RefreshDrives();
    }

    private void Log(string s)
    {
        _txtLog.AppendText(s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
        Debug.WriteLine(s);
    }
}
