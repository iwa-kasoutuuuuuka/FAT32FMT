using System.Diagnostics;
using Fat32Fmt.Core;
using Fat32Fmt.Core.Diagnostics;
using Fat32Fmt.Core.Fat32;
using Fat32Fmt.Core.Formatting;
using Fat32Fmt.Core.Imaging;
using Fat32Fmt.Core.IO;
using Fat32Fmt.Core.Recovery;
using Fat32Fmt.Core.Windows;

namespace Fat32Fmt.Gui;

/// <summary>データ管理: カード全体のバックアップ / 書き戻し / 消去と、削除ファイルの復旧。</summary>
public sealed class DataForm : Form
{
    private readonly ComboBox _cmbDisk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _btnRefresh = new() { Text = "更新(&R)", AutoSize = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly TextBox _txtBackupPath = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBackup = new() { Text = "バックアップ開始(&B)", AutoSize = true };

    private readonly TextBox _txtRestorePath = new() { Dock = DockStyle.Fill };
    private readonly Button _btnRestore = new() { Text = "書き戻し開始(&W)", AutoSize = true };

    private readonly NumericUpDown _numPasses = new() { Minimum = 1, Maximum = SecureEraser.MaxPasses, Value = 1, Width = 60 };
    private readonly RadioButton _rdoZero = new() { Text = "0 で埋める", Checked = true, AutoSize = true };
    private readonly RadioButton _rdoRandom = new() { Text = "乱数で埋める", AutoSize = true };
    private readonly CheckBox _chkVerify = new() { Text = "最後のパスを読み戻して確認する", AutoSize = true };
    private readonly CheckBox _chkFormat = new() { Text = "消去後に MBR + FAT32 で初期化する（推奨。オフにするとパーティションの無い状態になります）", Checked = true, AutoSize = true };
    private readonly TextBox _txtWipeLabel = new() { Width = 140, MaxLength = 11 };
    private readonly Button _btnWipe = new() { Text = "消去開始(&E)", AutoSize = true };

    private readonly RadioButton _rdoRecoverCard = new() { Text = "選択中のカード", Checked = true, AutoSize = true };
    private readonly RadioButton _rdoRecoverImage = new() { Text = "イメージファイル:", AutoSize = true };
    private readonly TextBox _txtRecoverImage = new() { Width = 360 };
    private readonly Button _btnScan = new() { Text = "削除ファイルを検索(&S)", AutoSize = true };
    private readonly ListView _lstFiles = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
    private readonly TextBox _txtRecoverTo = new() { Dock = DockStyle.Fill };
    private readonly Button _btnRecover = new() { Text = "チェックしたファイルを復旧(&V)", AutoSize = true };

    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "準備完了" };
    private readonly Button _btnCancel = new() { Text = "中止", AutoSize = true, Enabled = false };
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9F) };

    private readonly char? _initialLetter;
    private List<DiskInfo> _disks = new();
    private List<DeletedFile> _found = new();
    private string? _scannedSource;
    private CancellationTokenSource? _cts;

    public DataForm(char? initialLetter)
    {
        _initialLetter = initialLetter;
        Text = "データ管理 - バックアップ / 書き戻し / 消去 / 削除ファイル復旧";
        Font = new Font("Yu Gothic UI", 9F);
        MinimumSize = new Size(760, 640);
        Size = new Size(900, 720);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();

        _btnRefresh.Click += (_, _) => RefreshDisks();
        _btnBackup.Click += async (_, _) => await BackupAsync();
        _btnRestore.Click += async (_, _) => await RestoreAsync();
        _btnWipe.Click += async (_, _) => await WipeAsync();
        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnRecover.Click += async (_, _) => await RecoverAsync();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _cmbDisk.SelectedIndexChanged += (_, _) => UpdateDefaults();
        FormClosing += (_, e) =>
        {
            if (_cts == null) return;
            e.Cancel = true;
            MessageBox.Show(this, "処理中は閉じられません。「中止」を押して処理が止まってから閉じてください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Load += (_, _) =>
        {
            if (!VolumeFormatService.IsElevated())
                Log("警告: 管理者権限で実行されていません。カードの読み書きはできません（イメージファイルからの復旧のみ可能）。");
            RefreshDisks();
        };
    }

    // ---- 画面構成 -------------------------------------------------------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "対象カード(&D):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(_cmbDisk, 1, 0);
        top.Controls.Add(_btnRefresh, 2, 0);
        root.Controls.Add(top, 0, 0);

        _tabs.TabPages.Add(BackupPage());
        _tabs.TabPages.Add(RestorePage());
        _tabs.TabPages.Add(WipePage());
        _tabs.TabPages.Add(RecoverPage());
        root.Controls.Add(_tabs, 0, 1);

        var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        status.Controls.Add(_progress, 0, 0);
        status.Controls.Add(_btnCancel, 1, 0);
        status.Controls.Add(_lblStatus, 0, 1);
        root.Controls.Add(status, 0, 2);
        root.Controls.Add(_txtLog, 0, 3);
        Controls.Add(root);
    }

    private static TableLayoutPanel Grid(int columns)
    {
        var g = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, Padding = new Padding(8) };
        for (int i = 0; i < columns; i++)
            g.ColumnStyles.Add(i == 1 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };

    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 8) };

    private TabPage BackupPage()
    {
        var g = Grid(3);
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "ディスクイメージ (*.img)|*.img", DefaultExt = "img", AddExtension = true, FileName = Path.GetFileName(_txtBackupPath.Text), OverwritePrompt = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtBackupPath.Text = dlg.FileName;
        };
        g.Controls.Add(Lbl("保存先(&O):"), 0, 0);
        g.Controls.Add(_txtBackupPath, 1, 0);
        g.Controls.Add(browse, 2, 0);
        var note = Note("カード全体（MBR とすべてのパーティション）をイメージファイルに保存します。カードの内容は変更しません。" +
                        "0 だけの領域は NTFS のスパースファイルの穴になるので、空きの多いカードは小さく保存されます。SHA-256 を <イメージ>.sha256 に記録します。");
        g.Controls.Add(note, 0, 1);
        g.SetColumnSpan(note, 3);
        g.Controls.Add(_btnBackup, 0, 2);
        return new TabPage("バックアップ") { Controls = { g } };
    }

    private TabPage RestorePage()
    {
        var g = Grid(3);
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "ディスクイメージ (*.img)|*.img|すべてのファイル|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtRestorePath.Text = dlg.FileName;
        };
        g.Controls.Add(Lbl("イメージ(&I):"), 0, 0);
        g.Controls.Add(_txtRestorePath, 1, 0);
        g.Controls.Add(browse, 2, 0);
        var note = Note("イメージをカード全体に書き戻します（カードの内容はすべて置き換わります）。書き込み後にカードを読み戻して SHA-256 を照合し、" +
                        ".sha256 ファイルがあればバックアップ時から変わっていないかも確認します。");
        g.Controls.Add(note, 0, 1);
        g.SetColumnSpan(note, 3);
        g.Controls.Add(_btnRestore, 0, 2);
        return new TabPage("書き戻し") { Controls = { g } };
    }

    private TabPage WipePage()
    {
        var g = Grid(2);
        g.Controls.Add(Lbl("上書き回数(&P):"), 0, 0);
        g.Controls.Add(_numPasses, 1, 0);
        g.Controls.Add(Lbl("パターン:"), 0, 1);
        g.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { _rdoZero, _rdoRandom } }, 1, 1);
        g.Controls.Add(_chkVerify, 1, 2);
        g.Controls.Add(_chkFormat, 1, 3);
        g.Controls.Add(Lbl("初期化時のラベル:"), 0, 4);
        g.Controls.Add(_txtWipeLabel, 1, 4);
        var note = Note("カード全体のすべてのセクタを上書きします。パーティション情報も消えるので、通常は「初期化する」をオンのままにしてください" +
                        "（オフの場合、Windows はディスク全体を未フォーマットのボリュームとして表示します）。" +
                        "注意: SD カードはウェアレベリングのため、予備ブロックに古いデータが残る可能性があります。");
        g.Controls.Add(note, 0, 5);
        g.SetColumnSpan(note, 2);
        g.Controls.Add(_btnWipe, 0, 6);
        _chkFormat.CheckedChanged += (_, _) => _txtWipeLabel.Enabled = _chkFormat.Checked;
        return new TabPage("消去") { Controls = { g } };
    }

    private TabPage RecoverPage()
    {
        var g = Grid(3);
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var src = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var browseImg = new Button { Text = "参照...", AutoSize = true };
        browseImg.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "ディスクイメージ (*.img)|*.img|すべてのファイル|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) { _txtRecoverImage.Text = dlg.FileName; _rdoRecoverImage.Checked = true; }
        };
        src.Controls.AddRange([_rdoRecoverCard, _rdoRecoverImage, _txtRecoverImage, browseImg, _btnScan]);
        g.Controls.Add(src, 0, 0);
        g.SetColumnSpan(src, 3);

        _lstFiles.Columns.Add("パス", 380);
        _lstFiles.Columns.Add("サイズ", 90, HorizontalAlignment.Right);
        _lstFiles.Columns.Add("見込み", 90);
        _lstFiles.Columns.Add("更新日時", 130);
        g.Controls.Add(_lstFiles, 0, 1);
        g.SetColumnSpan(_lstFiles, 3);

        var browseTo = new Button { Text = "参照...", AutoSize = true };
        browseTo.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "復旧したファイルの保存先（復旧元とは別のドライブ）" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtRecoverTo.Text = dlg.SelectedPath;
        };
        g.Controls.Add(Lbl("保存先(&T):"), 0, 2);
        g.Controls.Add(_txtRecoverTo, 1, 2);
        g.Controls.Add(browseTo, 2, 2);
        g.Controls.Add(_btnRecover, 0, 3);
        g.SetColumnSpan(_btnRecover, 2);
        return new TabPage("削除ファイル復旧") { Controls = { g } };
    }

    // ---- 対象ディスク -----------------------------------------------------------

    private DiskInfo? SelectedDisk => _cmbDisk.SelectedIndex >= 0 && _cmbDisk.SelectedIndex < _disks.Count ? _disks[_cmbDisk.SelectedIndex] : null;

    /// <summary>リムーバブルの物理ディスクを列挙する（消去後でドライブ文字の無いカードも含める）。</summary>
    private void RefreshDisks()
    {
        int? keep = SelectedDisk?.DiskNumber;
        var list = new List<DiskInfo>();
        for (int n = 0; n < 32; n++)
        {
            try
            {
                var d = DiskTargets.Describe(n);
                if (d.IsRemovable && !d.IsSystemDisk) list.Add(d); // 保護対象のディスクは出さない
            }
            catch (VolumeFormatException) { }
        }
        _disks = list;
        _cmbDisk.Items.Clear();
        foreach (var d in list) _cmbDisk.Items.Add(d.Display);
        int idx = keep is int k ? list.FindIndex(d => d.DiskNumber == k)
                : _initialLetter is char l ? list.FindIndex(d => d.Letters.Contains(l)) : -1;
        _cmbDisk.SelectedIndex = idx >= 0 ? idx : list.Count > 0 ? 0 : -1;
        if (list.Count == 0) Log("リムーバブルディスクが見つかりません。カードを挿入して「更新」を押してください。");
        UpdateDefaults();
    }

    private void UpdateDefaults()
    {
        var d = SelectedDisk;
        if (d == null) return;
        if (_txtBackupPath.Text.Length == 0)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _txtBackupPath.Text = Path.Combine(dir, $"card-{DateTime.Now:yyyyMMdd-HHmm}.img");
        }
        _rdoRecoverCard.Text = d.Letters.Count > 0 ? $"選択中のカード ({d.Letters[0]}:)" : "選択中のカード（ドライブ文字なし）";
        _rdoRecoverCard.Enabled = d.Letters.Count > 0;
        if (!_rdoRecoverCard.Enabled) _rdoRecoverImage.Checked = true;
    }

    // ---- 各操作 ---------------------------------------------------------------

    private async Task BackupAsync()
    {
        if (RequireDisk() is not { } d) return;
        string path = _txtBackupPath.Text.Trim();
        if (path.Length == 0) { Warn("保存先を指定してください。"); return; }
        path = Path.GetFullPath(path);
        if (DiskTargets.DiskNumberOfPath(path) == d.DiskNumber) { Warn("保存先がバックアップ元と同じカード上にあります。別のドライブを指定してください。"); return; }
        if (File.Exists(path) && MessageBox.Show(this, $"{path} を上書きしますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        await RunAsync($"Disk {d.DiskNumber} を {path} にバックアップ", (progress, log, ct) =>
        {
            using var disk = PhysicalDisk.Open(d.DiskNumber, writable: false, log);
            if (File.Exists(path)) File.Delete(path);
            try
            {
                ImageResult r;
                using (var image = SparseImage.Create(path, disk.Length, disk.SectorSize))
                    r = DiskImager.Backup(disk, image, imageIsZeroFilled: true, progress, ct);
                ImageHashFile.Write(path, r.Sha256);
                return $"バックアップ完了: {SizeFormat.Both(r.Bytes)}、{r.Elapsed.TotalSeconds:0} 秒\r\nSHA-256: {r.Sha256}";
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        });
    }

    private async Task RestoreAsync()
    {
        if (RequireDisk() is not { } d) return;
        string path = _txtRestorePath.Text.Trim();
        if (!File.Exists(path)) { Warn("イメージファイルを指定してください。"); return; }
        path = Path.GetFullPath(path);
        if (DiskTargets.DiskNumberOfPath(path) == d.DiskNumber) { Warn("イメージが書き戻し先と同じカード上にあります。"); return; }
        if (!ConfirmDestructive(d, $"イメージ {Path.GetFileName(path)} ({SizeFormat.Both(new FileInfo(path).Length)}) で、カードの内容をすべて置き換えます。")) return;

        await RunAsync($"{path} を Disk {d.DiskNumber} に書き戻し", (progress, log, ct) =>
        {
            string? recorded = ImageHashFile.TryRead(path);
            RestoreResult r;
            using (var image = StreamBlockDevice.OpenImage(path, writable: false, d.SectorSize))
            using (var disk = PhysicalDisk.Open(d.DiskNumber, writable: true, log))
                r = DiskImager.Restore(image, disk, recorded, progress, log, ct);
            var letters = DiskTargets.WaitForLetters(d.DiskNumber, TimeSpan.FromSeconds(15));
            return $"書き戻し完了: {SizeFormat.Both(r.Bytes)}、{r.Elapsed.TotalSeconds:0} 秒\r\n" +
                   $"照合 OK（読み戻した SHA-256 がイメージと一致）\r\n" +
                   (recorded == null ? "（.sha256 が無いため、バックアップ時との比較はしていません）\r\n"
                    : r.MatchesRecordedHash ? "イメージはバックアップ時の記録と一致しました。\r\n" : "警告: イメージがバックアップ時の記録と一致しません！\r\n") +
                   (letters.Count > 0 ? $"再マウント: {string.Join(", ", letters.Select(l => l + ":"))}" : "ドライブ文字はまだ割り当てられていません。");
        });
    }

    private async Task WipeAsync()
    {
        if (RequireDisk() is not { } d) return;
        string? label = _chkFormat.Checked && _txtWipeLabel.Text.Trim().Length > 0 ? _txtWipeLabel.Text.Trim() : null;
        if (!VolumeLabel.TryValidate(label, out string? labelError)) { Warn(labelError!); return; }
        var opt = new WipeOptions
        {
            Passes = (int)_numPasses.Value,
            Pattern = _rdoRandom.Checked ? WipePattern.Random : WipePattern.Zero,
            Verify = _chkVerify.Checked,
        };
        bool format = _chkFormat.Checked;
        if (!ConfirmDestructive(d, $"カード全体を {opt.Passes} 回上書きして消去します（{(_rdoRandom.Checked ? "乱数" : "0")}）。元に戻せません。")) return;

        await RunAsync($"Disk {d.DiskNumber} を消去", (progress, log, ct) =>
        {
            WipeResult r;
            using (var disk = PhysicalDisk.Open(d.DiskNumber, writable: true, log))
            {
                r = SecureEraser.Wipe(disk, opt, progress, log, ct);
                if (format) DiskInitializer.InitializeMbrFat32(disk, label, progress, ct);
                else disk.DeletePartitionTable(log);
            }
            var letters = DiskTargets.WaitForLetters(d.DiskNumber, TimeSpan.FromSeconds(format ? 15 : 5));
            return $"消去完了: {SizeFormat.Both(r.Bytes)} × {r.Passes} 回、{r.Elapsed.TotalSeconds:0} 秒" +
                   (r.Verified ? "\r\n読み戻し確認 OK" : "") +
                   (format ? $"\r\nFAT32 で初期化しました{(letters.Count > 0 ? $": {string.Join(", ", letters.Select(l => l + ":"))}" : "")}"
                    : "\r\nパーティションはありません。" + (letters.Count > 0 ? "\r\n" + DiskTargets.SuperfloppyNote(letters) : ""));
        });
    }

    private async Task ScanAsync()
    {
        string? source = RecoverSource();
        if (source == null) return;
        _lstFiles.Items.Clear();
        List<DeletedFile>? found = null;
        await RunAsync($"{source} の削除ファイルを検索", (_, _, ct) =>
        {
            using var dev = OpenRecoverSource(source, out var volume);
            found = DeletedFileRecovery.Scan(volume, ct);
            return $"削除ファイル {found.Count} 個";
        }, showDialog: false);
        if (found == null) return;
        _found = found;
        _scannedSource = source;
        foreach (var f in found)
        {
            var item = new ListViewItem([f.Path + (f.NameRecovered ? "" : "  (先頭文字は推定不可)"), SizeFormat.Binary(f.Size),
                DeletedFileRecovery.ChanceName(f.Chance), f.Entry.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""])
            {
                Checked = f.Chance is RecoveryChance.Good or RecoveryChance.Partial or RecoveryChance.Empty,
                Tag = f,
            };
            if (f.Chance is RecoveryChance.Overwritten or RecoveryChance.Invalid) item.ForeColor = SystemColors.GrayText;
            _lstFiles.Items.Add(item);
        }
    }

    private async Task RecoverAsync()
    {
        if (_scannedSource == null) { Warn("先に「削除ファイルを検索」を実行してください。"); return; }
        var files = _lstFiles.CheckedItems.Cast<ListViewItem>().Select(i => (DeletedFile)i.Tag!).ToList();
        if (files.Count == 0) { Warn("復旧するファイルにチェックを付けてください。"); return; }
        string dest = _txtRecoverTo.Text.Trim();
        if (dest.Length == 0) { Warn("保存先フォルダを指定してください。"); return; }
        dest = Path.GetFullPath(dest);
        string source = _scannedSource;
        if (source.Length == 2 && char.ToUpperInvariant(dest[0]) == source[0]) { Warn("保存先が復旧元と同じドライブです。別のドライブを指定してください。"); return; }
        if (source.Length == 2 && DiskTargets.DiskNumberOfPath(dest) is int dn && dn == DiskTargets.DiskNumberOfPath(source + "\\"))
        { Warn("保存先が復旧元と同じ物理ディスク上にあります。別のドライブを指定してください。"); return; }

        await RunAsync($"{files.Count} 個のファイルを {dest} に復旧", (progress, log, ct) =>
        {
            using var dev = OpenRecoverSource(source, out var volume);
            var r = DeletedFileRecovery.Recover(volume, files, dest, progress, log, ct);
            return $"{r.Count} 個（{SizeFormat.Binary(r.Sum(x => x.Bytes))}）を {dest} に保存しました。\r\n" +
                   "連続クラスタを仮定しているため、断片化していたファイルは後半が壊れている場合があります。";
        });
        try { Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true }); } catch { }
    }

    private string? RecoverSource()
    {
        if (_rdoRecoverImage.Checked)
        {
            string p = _txtRecoverImage.Text.Trim();
            if (!File.Exists(p)) { Warn("イメージファイルを指定してください。"); return null; }
            return Path.GetFullPath(p);
        }
        if (SelectedDisk is not { Letters.Count: > 0 } d) { Warn("ドライブ文字のあるカードを選択してください。"); return null; }
        return $"{d.Letters[0]}:";
    }

    /// <summary>"E:" ならボリュームを読み取り専用で、それ以外はイメージファイル（MBR 付きならパーティション 1）を開く。</summary>
    private static IBlockDevice OpenRecoverSource(string source, out Fat32Volume volume)
    {
        if (source.Length == 2 && source[1] == ':')
        {
            var v = ReadOnlyVolume.Open(source[0]);
            try { volume = Fat32Volume.Open(v); }
            catch { v.Dispose(); throw; }
            return v;
        }
        var image = StreamBlockDevice.OpenImage(source, writable: false);
        try { volume = Fat32Volume.Open(Fat32Verifier.LocateVolume(image, out _)); }
        catch { image.Dispose(); throw; }
        return image;
    }

    // ---- 共通 ----------------------------------------------------------------

    private DiskInfo? RequireDisk()
    {
        var d = SelectedDisk;
        if (d == null) { Warn("対象カードを選択してください。"); return null; }
        if (!VolumeFormatService.IsElevated()) { Warn("管理者権限が必要です。FAT32FMT を管理者として実行してください。"); return null; }
        try { DiskTargets.Validate(d, allowFixed: false); }
        catch (VolumeFormatException ex) { Warn(ex.Message); return null; }
        return d;
    }

    private bool ConfirmDestructive(DiskInfo d, string what) =>
        MessageBox.Show(this, $"{d.Display}\r\n\r\n{what}\r\n\r\n本当に続行しますか？", "確認",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private async Task RunAsync(string title, Func<IProgress<OperationProgress>, Action<string>, CancellationToken, string> work, bool showDialog = true)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _progress.Value = 0;
        Log($"=== {title} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        var progress = new Progress<OperationProgress>(p =>
        {
            _progress.Value = Math.Clamp((int)(p.Fraction * 1000), 0, 1000);
            string speed = p.BytesPerSecond > 0 ? $"  {SizeFormat.Speed(p.BytesPerSecond)}" : "";
            string eta = p.Remaining is { } rem ? $"  残り約 {rem:hh\\:mm\\:ss}" : "";
            _lblStatus.Text = p.Total > 0 ? $"{p.Phase}  {p.Fraction * 100:0.0}%  ({SizeFormat.Binary(p.Done)} / {SizeFormat.Binary(p.Total)}){speed}{eta}" : p.Phase;
        });
        var ct = _cts.Token;
        try
        {
            string summary = await Task.Run(() => work(progress, s => BeginInvoke(() => Log(s)), ct), ct);
            _progress.Value = 1000;
            _lblStatus.Text = "完了";
            Log(summary);
            if (showDialog) MessageBox.Show(this, summary, "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "中止されました";
            Log("ユーザーにより中止されました。");
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "エラー";
            string m = ex is VolumeFormatException or Fat32LayoutException or ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
                ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log("エラー: " + m);
            MessageBox.Show(this, m, "失敗しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        _cmbDisk.Enabled = _btnRefresh.Enabled = _btnBackup.Enabled = _btnRestore.Enabled = _btnWipe.Enabled =
            _btnScan.Enabled = _btnRecover.Enabled = !busy;
        _btnCancel.Enabled = busy;
        UseWaitCursor = busy;
    }

    private void Log(string s)
    {
        _txtLog.AppendText(s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
        Debug.WriteLine(s);
    }
}
