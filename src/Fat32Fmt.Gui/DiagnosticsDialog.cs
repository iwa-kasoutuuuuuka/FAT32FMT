using Fat32Fmt.Core;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Gui;

/// <summary>カードの検査（速度測定・不良セクタスキャン・容量偽装チェック）を行うダイアログ。</summary>
public sealed class DiagnosticsDialog : Form
{
    private static readonly DiagnosticKind[] Kinds =
    {
        DiagnosticKind.Bench, DiagnosticKind.ScanRead, DiagnosticKind.CapacityQuick,
        DiagnosticKind.CapacityFull, DiagnosticKind.BenchWrite, DiagnosticKind.ScanWrite,
    };

    private readonly DriveEntry _drive;
    private readonly ComboBox _cmbKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _lblKindHelp = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Button _btnStart = new() { Text = "検査開始(&S)", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
    private readonly Button _btnCancel = new() { Text = "中止", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000 };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "準備完了" };
    private readonly TextBox _txtResult = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9F), WordWrap = false };
    private CancellationTokenSource? _cts;

    /// <summary>破壊的な検査を実行した（カードのファイルシステムが失われた）。</summary>
    public bool DestroyedData { get; private set; }

    public DiagnosticsDialog(DriveEntry drive)
    {
        _drive = drive;
        Text = $"カードの検査 - {drive.Letter}:";
        Font = new Font("Yu Gothic UI", 9F);
        MinimumSize = new Size(700, 560);
        Size = new Size(780, 640);
        StartPosition = FormStartPosition.CenterParent;

        foreach (var k in Kinds)
            _cmbKind.Items.Add(DiagnosticRequest.KindName(k) + (DiagnosticRequest.IsDestructiveKind(k) ? "  ※データ消去" : "  （非破壊）"));
        _cmbKind.SelectedIndex = 0;
        _cmbKind.SelectedIndexChanged += (_, _) => UpdateHelp();
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        FormClosing += (_, e) =>
        {
            if (_cts == null) return;
            e.Cancel = true;
            MessageBox.Show(this, "検査中は閉じられません。「中止」を押して止まってから閉じてください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        void AddRow(string label, Control c, SizeType st = SizeType.AutoSize, float h = 0)
        {
            root.RowStyles.Add(new RowStyle(st, h));
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
            c.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(c, 1, row++);
        }
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _btnStart.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.Add(_btnStart);
        buttons.Controls.Add(_btnCancel);

        AddRow("対象:", new Label { Text = drive.Display, AutoSize = true });
        AddRow("検査(&K):", _cmbKind);
        AddRow("", _lblKindHelp);
        AddRow("", buttons);
        AddRow("進行状況:", _progress, SizeType.Absolute, 28);
        AddRow("", _lblStatus);
        AddRow("結果:", _txtResult, SizeType.Percent, 100);
        Controls.Add(root);
        UpdateHelp();
    }

    private DiagnosticKind SelectedKind => Kinds[Math.Max(0, _cmbKind.SelectedIndex)];

    private void UpdateHelp()
    {
        _lblKindHelp.Text = SelectedKind switch
        {
            DiagnosticKind.Bench => "連続とランダム 4K の読み取り速度を測ります。スピードクラスの判定には「速度測定（読み書き）」が必要です。",
            DiagnosticKind.BenchWrite => "書き込み速度も測り、Class 10 / U1 / U3 / V30 などの基準と比べます。先頭 約 1 GiB を上書きします。",
            DiagnosticKind.ScanRead => "カード全体を読み、読めないセクタと読み取りが遅い区間を探します。データはそのままです（時間がかかります）。",
            DiagnosticKind.ScanWrite => "全セクタにパターンを書いて読み戻し、保存できないセクタを探します。",
            DiagnosticKind.CapacityQuick => "約 500 か所に位置ごとに異なるデータを書いて読み戻し、容量偽装（フェイクカード）を数分で調べます。",
            _ => "H2testw と同じく全域に書いてから全域を読み戻します。確実ですが、128 GB で数時間かかります。",
        };
    }

    private async Task StartAsync()
    {
        var req = new DiagnosticRequest { DriveLetter = _drive.Letter, Kind = SelectedKind, AllowFixedDisk = _drive.Kind == DriveKind.Fixed };
        try
        {
            DiagnosticsService.ValidateTarget(req);
        }
        catch (Exception ex) when (ex is VolumeFormatException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "検査できません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (req.IsDestructive)
        {
            string msg = $"{DiagnosticRequest.KindName(req.Kind)} はカードに検査パターンを書き込みます。\r\n" +
                         $"ドライブ {_drive.Letter}: 上のデータはすべて消去され、検査後はフォーマットが必要です。\r\n\r\n{_drive.Display}\r\n\r\n本当に続行しますか？";
            if (MessageBox.Show(this, msg, "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _txtResult.Clear();
        _progress.Value = 0;
        var progress = new Progress<OperationProgress>(p =>
        {
            _progress.Value = Math.Clamp((int)(p.Fraction * 1000), 0, 1000);
            string speed = p.BytesPerSecond > 0 && !p.Phase.Contains("ランダム") ? $"  {SizeFormat.Speed(p.BytesPerSecond)}" : "";
            string eta = p.Remaining is { } rem ? $"  残り約 {rem:hh\\:mm\\:ss}" : "";
            _lblStatus.Text = $"{p.Phase}  {p.Fraction * 100:0.0}%{speed}{eta}";
        });
        var ct = _cts.Token;
        try
        {
            if (req.IsDestructive) DestroyedData = true;
            var outcome = await Task.Run(() => DiagnosticsService.Run(req, progress, s => BeginInvoke(() => AppendLog(s)), ct), ct);
            _progress.Value = 1000;
            _lblStatus.Text = outcome.ProblemFound ? "完了（問題あり）" : "完了";
            AppendLog("");
            AppendLog(outcome.Describe());
            if (req.IsDestructive) AppendLog(DiagnosticsService.DestroyedNotice);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "中止されました";
            AppendLog("中止されました。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "エラー";
            string m = ex is VolumeFormatException or ArgumentException or IOException or InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            AppendLog("エラー: " + m);
            MessageBox.Show(this, m, "検査に失敗しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _cmbKind.Enabled = _btnStart.Enabled = !busy;
        _btnCancel.Enabled = busy;
        UseWaitCursor = busy;
    }

    private void AppendLog(string s) =>
        _txtResult.AppendText(s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
}
