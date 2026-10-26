# FAT32FMT

SDXC など **32 GB を超える SD カード／USB メモリを FAT32 でフォーマット**するための Windows 向けツールです。
Windows 標準のフォーマット機能は 32 GB を超えるボリュームを FAT32 にできませんが、FAT32 自体は 2 TB まで扱えます。
カメラ・カーナビ・ゲーム機・3D プリンタなど、exFAT 非対応で FAT32 を要求する機器向けです。

- **GUI アプリ**（`FAT32FMT.exe`）: ドライブを選んでボタン一つ。管理者権限で起動（UAC）。
- **CLI**（`fat32fmt.exe`）: スクリプトや AI エージェントから使う。JSON 出力対応。
- **MCP サーバー**（`fat32fmt.exe mcp`）: Claude Code / Antigravity などから直接ツールとして呼べる。

リポジトリ: <https://github.com/iwa-kasoutuuuuuka/FAT32FMT>

## ダウンロード

ビルド済みのバイナリは [Releases](https://github.com/iwa-kasoutuuuuuka/FAT32FMT/releases/latest) から入手できます（Windows 10 / 11 x64）。

| ファイル | 内容 |
| --- | --- |
| [FAT32FMT-v1.0.1-win-x64.zip](https://github.com/iwa-kasoutuuuuuka/FAT32FMT/releases/download/v1.0.1/FAT32FMT-v1.0.1-win-x64.zip)（約 86 MB） | **通常はこちら。** .NET ランタイムを同梱した単体実行ファイル。インストール不要 |
| [FAT32FMT-v1.0.1-win-x64-framework-dependent.zip](https://github.com/iwa-kasoutuuuuuka/FAT32FMT/releases/download/v1.0.1/FAT32FMT-v1.0.1-win-x64-framework-dependent.zip)（約 350 KB） | 軽量版。[.NET 8 デスクトップ ランタイム](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）が必要 |

zip を展開すると次の構成になります。GUI の `FAT32FMT.exe` と CLI の `fat32fmt.exe` は Windows では大文字小文字を区別しないため同じ名前になるので、フォルダを分けています。

```
GUI\FAT32FMT.exe    GUI アプリ（ダブルクリックで起動。UAC で「はい」）
CLI\fat32fmt.exe    CLI / MCP サーバー
LICENSE
```

SmartScreen の警告が出た場合は「詳細情報」→「実行」で起動できます（コード署名はしていません）。

## 特徴

| 項目 | 内容 |
| --- | --- |
| 対応容量 | 約 33 MB 〜 2 TB（セクタ 512 B の場合） |
| クラスタサイズ | 自動（Windows 既定表。32 GB 超は 32 KB）または 512 B〜64 KB を指定 |
| SD 規格に沿った配置 | データ領域の開始を SD の境界単位 (BU: 4 / 16 / 32 MB) に整列し、書き込み性能と寿命を確保 |
| パーティション種別の修正 | exFAT 由来の MBR 種別 0x07 を FAT32 LBA (0x0C) に書き換え（機器の互換性向上） |
| クイック／通常 | クイックはシステム領域のみ初期化。通常は全領域を書き込み検査し不良クラスタを FAT に登録（失敗に時間がかかる区間は丸ごと不良として先へ進み、それが 8 回続いたら「カードが応答しない」として中止） |
| ラベル | 11 バイト以内。日本語（CP932）も可 |
| 安全装置 | リムーバブルのみ列挙（固定ディスクは明示的に許可が必要）、Windows のある物理ディスクと起動用パーティション（EFI など）のある物理ディスクを拒否（判定できないときも拒否）、書き込み禁止（ロックスイッチ）の検出、確認入力、UAC。管理者権限で書き込む場所の制限とカード由来の文字列の無害化は「[セキュリティ](#セキュリティ)」を参照 |
| 書き込み後の検証 | フォーマット直後にブートセクタ・バックアップ・FSInfo・FAT の全コピー・ルートディレクトリを読み戻して検査 |
| カードの検査 | 容量偽装（フェイクカード）チェック（H2testw / F3 方式）、実測の速度テストとスピードクラスの目安、不良セクタスキャン（非破壊／破壊） |
| バックアップ／書き戻し | カード全体（MBR を含む物理ディスク）を `.img`（スパースファイル）に保存し SHA-256 を記録。書き戻し後は読み戻してハッシュ照合 |
| セキュア消去 | 0 埋め／乱数埋めで指定回数上書き。読み戻し確認、消去後の MBR + FAT32 初期化も可 |
| 削除ファイルの復旧 | FAT32 の削除エントリを探し、LFN のチェックサムから名前を復元して別ドライブへ取り出す |

## 構成

```
FAT32FMT/
├─ FAT32FMT.slnx
├─ src/
│  ├─ Fat32Fmt.Core/              コアライブラリ
│  │   ├─ IO/                     IBlockDevice とストリーム／サブデバイス実装
│  │   ├─ Fat32/                  レイアウト計算・ブートセクタ・FAT 書き込み・ボリューム読み取り
│  │   ├─ Diagnostics/            FAT32 構造の読み戻し検証 (Fat32Verifier)、容量偽装チェック (CapacityTester)、
│  │                              速度テスト (SpeedBenchmark)、不良セクタスキャン (SurfaceScanner)
│  │   ├─ Imaging/                ディスク⇔イメージのコピーとハッシュ照合 (DiskImager)、.sha256 ファイル
│  │   ├─ Formatting/             上書き消去 (SecureEraser)、空ディスクへの MBR + FAT32 作成 (DiskInitializer)
│  │   ├─ Recovery/               削除ファイルの検索と取り出し (DeletedFileRecovery)
│  │   └─ Windows/                Windows 固有: ボリュームのロック/マウント解除、ドライブ列挙、
│  │                              MBR 種別修正、UAC 昇格実行 (ElevatedCli)、フォーマットサービス、
│  │                              物理ディスク全体の読み書き (PhysicalDisk: 全ボリュームをロックしてから書く)
│  ├─ Fat32Fmt.Cli/               CLI (fat32fmt.exe) と MCP サーバー
│  └─ Fat32Fmt.Gui/               WinForms GUI (FAT32FMT.exe)
└─ tests/Fat32Fmt.Tests/          xUnit テスト
```

## ビルド

.NET 8 SDK 以降が必要です。

```powershell
dotnet build FAT32FMT.slnx -c Release
```

成果物:

- GUI: `src\Fat32Fmt.Gui\bin\Release\net8.0-windows\FAT32FMT.exe`
- CLI: `src\Fat32Fmt.Cli\bin\Release\net8.0-windows\fat32fmt.exe`

テスト:

```powershell
dotnet test FAT32FMT.slnx
```

配布用の単体実行ファイル（Releases の zip と同じもの）を作る場合:

```powershell
# ランタイム同梱版（GUI と CLI は同名になるので出力先を分ける）
dotnet publish src\Fat32Fmt.Gui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\GUI
dotnet publish src\Fat32Fmt.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\CLI
```

軽量版は `--self-contained true` 以降のランタイム関連の指定を `-p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none` に置き換えます。同じフォルダでランタイム同梱版を作った直後は `obj` の中間ファイルが再利用されて同梱版になってしまうことがあるため、`-p:IntermediateOutputPath=obj\fd\ -p:OutputPath=bin\fd\` のように中間フォルダを分けてください。

## GUI の使い方

1. `FAT32FMT.exe` を起動（UAC が出るので「はい」）。
2. ドライブを選ぶ（リムーバブルのみ表示。カードの抜き差しで一覧は自動更新。内蔵ディスクを対象にしたい場合のみ「固定ディスクも表示」）。
3. 必要ならクラスタサイズ・ラベルを変更。「対象の情報」にフォーマット後のレイアウトが表示されます。
4. 「フォーマット開始」→ 確認ダイアログで「はい」。

カードを調べるときは「検査...」ボタンで検査ダイアログを開き、速度測定・不良セクタスキャン・容量偽装チェックを選んで「検査開始」（内容は下の「カードの検査」）。

「データ管理...」ボタンでは、カード全体のバックアップ・書き戻し・消去と、削除ファイルの検索・復旧ができます（内容は下の「バックアップ・消去・削除ファイルの復旧」）。消去後でドライブ文字が無いカードも一覧に出ます。

コマンドラインで初期値を渡せます: `FAT32FMT.exe E: --label MYCARD --cluster 32K`

- フォーマット中はウィンドウを閉じられません（カードが中途半端な状態で残るのを防ぐため。止めるときは「中止」）。
- パーティション表の無いカード（消去後など。Windows はディスク全体を 1 ボリュームとして見せる）は「対象の情報」に「パーティション: なし」と表示されます。そのままでもフォーマットできますが、SD 規格どおりの MBR 形式にするには「データ管理」の消去で「消去後に MBR + FAT32 で初期化する」をオンにするか、`fat32fmt wipe diskN --format` を使ってください。

## CLI の使い方

```powershell
fat32fmt list                      # フォーマット可能なドライブ一覧
fat32fmt plan E:                   # 書き込まずにレイアウトを表示
fat32fmt format E: --label MYCARD  # フォーマット（確認入力あり。非管理者なら UAC で昇格）
fat32fmt format E: --yes --json    # 確認なし・JSON 出力（自動化向け）
fat32fmt image disk.img --size 64G # イメージファイルを FAT32 化（テスト用。スパースファイルなので空き容量以上でも可）
fat32fmt image sd.img --size 128000000000 --mbr  # SD カードと同じ MBR + パーティション形式で作成
fat32fmt verify sd.img             # イメージの FAT32 構造を検査（異常があれば終了コード 1）
fat32fmt version                   # バージョン表示
fat32fmt --help                    # 全コマンドとオプションの一覧
```

主なオプション: `--cluster 32K` `--label 名前` `--full`（全領域検査） `--no-ptype`（MBR 種別を変えない）
`--allow-fixed`（固定ディスク許可。`list` にも使える） `--align 16M`（整列境界。0 で無効） `--json` `--yes`
`--offset <バイト数>`（`image` でパーティション開始位置を指定。`--mbr` とは併用不可）

終了コード: 0=成功 1=エラー 2=使い方の誤り 3=キャンセル（UAC 拒否を含む） 4=検査で問題を検出（`test` / `bench`）

> 「上書きフォーマット」（SD Memory Card Formatter の上書きフォーマットに相当）は、`fat32fmt wipe E: --format` で全域を上書きしてから MBR + FAT32 で初期化します（[バックアップ・消去・削除ファイルの復旧](#バックアップ消去削除ファイルの復旧)を参照）。

管理者権限が無い場合、`format` は自分自身を UAC 昇格して起動し、結果を一時ファイル経由で受け取って表示します。
AI エージェントから呼ばれた場合も **人間が UAC で承認しなければ書き込みは始まりません**。

## カードの検査

```powershell
fat32fmt bench E:            # 実測の速度（連続・ランダム 4K の読み取り）。非破壊
fat32fmt bench E: --write    # 書き込みも測り、スピードクラスの目安を表示（先頭 約 1 GiB を上書き）
fat32fmt test E: --scan      # 不良セクタの読み取りスキャン（カード全体を読む。非破壊）
fat32fmt test E: --quick     # 容量偽装チェック（抜き取り、数分。test の既定）
fat32fmt test E: --full      # 容量偽装チェック（全域。32 GB で約 1 時間、128 GB で数時間）
fat32fmt test E: --scan --write  # 不良セクタの書き込みスキャン
fat32fmt test E: --scan --json   # 結果を JSON で
```

| 検査 | 方式 | データ |
| --- | --- | --- |
| 容量偽装チェック `--quick` / `--full` | 512 バイトごとに位置を埋め込んだ疑似乱数パターン（実行ごとに種を変える）を書き、**すべて書き終えてから**読み戻して照合。書き込みが実容量で折り返す偽装カードでは、ある位置を読むと別の位置のデータが返るので、埋め込んだ位置から折り返し幅＝実容量が分かる。一定位置より後ろが保存されない型も検出。`--quick` はディスク先頭基準で 2 のべき乗間隔に置いた約 500 個の標本だけを調べる（折り返し幅が 2 のべき乗なら検出できる）。結果は判定（正常／容量偽装の疑い／不良あり）・推定実容量・壊れている範囲・書き込み/読み取り速度 | **消去** |
| 速度テスト `bench` | 連続（4 MiB 単位、最大 512 MiB / 20 秒）とランダム 4K（先頭 1 GiB の範囲で 5 秒）を、OS キャッシュを通さず測定。書き込みの実測を Class 10 / U1 / U3 / V30 / V60 / V90 / A1 / A2 の最低基準と比べる | 読み取りのみは非破壊、`--write` は先頭を上書き |
| 不良セクタスキャン `--scan` | 1 MiB ずつ読み、エラーが出た区間は二分探索でセクタ単位まで絞り込む（不良 1 セクタあたり約 22 回の読み直し）。失敗までに 5 秒以上かかる区間は切り分けずに丸ごと不良とし、エラー処理の合計が 10 分を超えたら打ち切る（壊れかけのカードでも決まった時間で終わる）。読み取りに 0.5 秒以上かかる区間も「遅い区間」として報告。`--write` 付きは区間ごとにパターンを書いてすぐ読み戻す | 読み取りのみは非破壊、`--write` は消去 |

- 非破壊の検査はカード全体（`\\.\PhysicalDriveN`）をロックせずに読みます。破壊的な検査はフォーマットと同じくボリューム（パーティション）をロックして書くので、検査後もドライブ文字が残り、そのまま `fat32fmt format E:` できます。
- 破壊的な検査は format と同じ確認入力（`--yes` で省略）があります。どの検査も管理者権限が必要で、無ければ UAC で昇格します（進捗は元のウィンドウに表示、Ctrl+C で中止するとそこまでの結果を表示）。
- 終了コード: 0=問題なし、4=容量偽装または不良セクタを検出、3=中止、1=エラー。
- 規格のスピードクラスはカード内部の条件で測る最低保証値です。PC での実測はカードリーダーの性能にも左右されるため目安として扱ってください。

## バックアップ・消去・削除ファイルの復旧

```powershell
fat32fmt backup E: D:\backup\card.img          # カード全体を保存（card.img.sha256 にハッシュを記録）
fat32fmt restore D:\backup\card.img E:         # 書き戻し → 読み戻して SHA-256 照合（確認入力あり）
fat32fmt wipe E: --format --label MYCARD       # 0 で 1 回上書きし、MBR + FAT32 で初期化（通常はこれ。確認入力あり）
fat32fmt wipe E: --random --passes 3 --verify --format  # 乱数で 3 回、最後のパスを読み戻して確認してから初期化
fat32fmt wipe E:                               # 上書きのみ（パーティションの無い状態で終わる）
fat32fmt recover E: --list                     # 削除ファイルの一覧（読み取りのみ）
fat32fmt recover E: --to D:\recovered          # 別ドライブへ取り出す
fat32fmt recover D:\backup\card.img --to D:\recovered --match *.jpg   # バックアップイメージから取り出す
```

- **対象はカード全体（物理ディスク）です。** `E:` と指定すると、そのドライブがある物理ディスク（例: Disk 2）を対象にします。消去後などでドライブ文字が無いカードは `disk2` のように指定します（番号は wipe の完了メッセージ、GUI の「データ管理」の一覧、または PowerShell の `Get-Disk` で確認）。
- 書き込む操作（restore / wipe）は、ディスク上の全ボリュームをロックしてマウント解除してから `\\.\PhysicalDriveN` に書きます。終了時にパーティション情報を読み直させるので、Windows が新しい内容で再マウントします。backup は読み取り中の書き込みを防ぐためにボリュームをロックしますが、マウント解除はしません。
- **backup**: イメージは NTFS のスパースファイルなので、0 だけの領域はディスクを消費しません。同じディスク上への保存は拒否します。`--overwrite` で既存ファイルを上書き。
  ファイル名は拡張子 `.img` に限ります。また Windows / Program Files / ProgramData の中とネットワーク上のパスには保存できません（管理者権限で書き込むため。詳しくは「[セキュリティ](#セキュリティ)」）。
- **restore**: 書き込んだ範囲をカードから読み戻し、イメージと SHA-256 が一致しなければエラー（容量偽装カードや不良カードを検出できます）。`.sha256` があればイメージ自体がバックアップ時から変わっていないかも報告します。同じ公称容量でも実容量は個体差があるため、イメージの方が大きくても、はみ出す部分がすべて 0 なら切り捨てて書きます。
- **wipe**: `--passes N`（1〜35）回、0（既定）または `--random` で上書きします。パーティション情報も消えるので、**通常は `--format` を付けて MBR + FAT32 で初期化してください**。
  `--format` なしの場合は最後に `diskpart clean` と同じ方法で MBR の署名 55 AA を消し、Windows に「パーティションなし」を通知します。
  パーティションの無いリムーバブルディスクは、Windows がディスク全体を 1 つの未フォーマットのボリューム（オフセット 0。いわゆるスーパーフロッピー）として表示するので、ドライブ文字は残ります（正常な動作で、抜き差ししても変わりません）。後で `restore` するか、`wipe --format` で初期化してください。
  SD カードや USB メモリは内部でウェアレベリングを行うため、論理的に全域を上書きしても予備ブロックに古いデータが残る可能性があります。
- **recover**: ルートからすべてのディレクトリ（削除済みディレクトリの中も）をたどり、削除エントリを探します。
  - 名前: 削除で失われた短い名前の先頭 1 文字は、LFN（長いファイル名）のチェックサムから逆算します。LFN の無い 8.3 名だけのファイルは先頭が `_` になります。
  - 中身: FAT32 は削除時にクラスタチェーンを消すため、**先頭クラスタから連続して置かれていたと仮定**して読みます。断片化していたファイルは後半が別の内容になります。
  - 見込み: 該当クラスタがすべて未使用なら「良好」、一部が別ファイルに使われていれば「一部上書き」、先頭から使われていれば「上書き済み」（既定では取り出さない。`--all` で含める）。
  - 保存先は復旧元とは別のドライブに限ります（同じドライブに書くと、まだ残っている削除データを上書きしてしまうため）。Windows / Program Files / ProgramData の中とネットワーク上のパスも指定できません。
  - ファイル名に含まれる制御文字・文字の向きを変える文字は `_` に置き換え、`CON` や `NUL.txt` などの予約名は先頭に `_` を付けて保存します。
  - 誤フォーマット後のカードはディレクトリ自体が初期化されているので、この方法では復旧できません。
- 管理者権限: backup / restore / wipe とドライブからの recover は必要です（無ければ UAC で昇格。進捗とログは元のウィンドウに表示され、Ctrl+C で中止できます）。イメージファイルからの recover は不要です。

## Claude Code / Antigravity から操作する

### 方法 1: CLI をそのまま使う

エージェントにシェルから `fat32fmt list --json` → `fat32fmt plan E: --json` → `fat32fmt format E: --yes --json` を実行させるだけで動きます。

### 方法 2: MCP サーバーとして登録する

`fat32fmt.exe mcp` は stdio の MCP サーバーです。ツール:

| ツール | 内容 |
| --- | --- |
| `list_drives` | フォーマット可能なドライブ一覧（`includeFixed` で固定ディスクも表示。表示のみで、操作の許可にはならない） |
| `plan_format` | 書き込みなしの検証とレイアウト計算 |
| `format_drive` | フォーマット実行。`confirm` に `"FORMAT E"` のようにドライブ文字を含めた文字列が必須 |
| `benchmark_drive` | 実測の速度とスピードクラスの目安。既定は読み取りのみ。`write: true` は破壊的で `confirm: "ERASE E"` が必須 |
| `scan_drive` | 不良セクタスキャン。既定は読み取りのみ（非破壊）。`write: true` は破壊的で `confirm: "ERASE E"` が必須 |
| `test_capacity` | 容量偽装チェック（`mode`: `quick` / `full`）。常に破壊的で `confirm: "ERASE E"` が必須 |
| `backup_disk` | カード全体を `.img` に保存（カードは変更しない）。`confirm: "BACKUP E"` が必須 |
| `restore_image` | イメージを書き戻して照合。`confirm: "RESTORE E"`（ディスク番号指定なら `"RESTORE DISK2"`）が必須 |
| `wipe_disk` | 上書き消去（`passes` / `random` / `verify` / `format`）。`confirm: "WIPE E"` が必須 |
| `recover_deleted` | 削除ファイルの一覧（`listOnly`）と別ドライブへの取り出し。ドライブまたはイメージファイルが対象 |

以下の例はソースからビルドした場合のパスです。[Releases](https://github.com/iwa-kasoutuuuuuka/FAT32FMT/releases/latest) の zip を使う場合は、展開先の `CLI\fat32fmt.exe`（例: `C:\Tools\FAT32FMT\CLI\fat32fmt.exe`）に置き換えてください。

**Claude Code**（プロジェクトまたはユーザースコープ）:

```bash
claude mcp add fat32fmt -- "D:\FAT32FMT\src\Fat32Fmt.Cli\bin\Release\net8.0-windows\fat32fmt.exe" mcp
```

または `.mcp.json`:

```json
{
  "mcpServers": {
    "fat32fmt": {
      "command": "D:\\FAT32FMT\\src\\Fat32Fmt.Cli\\bin\\Release\\net8.0-windows\\fat32fmt.exe",
      "args": ["mcp"]
    }
  }
}
```

**Antigravity**（MCP 設定 `mcp_config.json`）:

```json
{
  "mcpServers": {
    "fat32fmt": {
      "command": "D:\\FAT32FMT\\src\\Fat32Fmt.Cli\\bin\\Release\\net8.0-windows\\fat32fmt.exe",
      "args": ["mcp"]
    }
  }
}
```

**固定ディスク（内蔵 HDD/SSD など）の扱い:** MCP サーバーは既定で固定ディスクを対象にできません。ツールの引数で `allowFixed` を指定しても拒否します（AI エージェントが指示文にだまされて内蔵ディスクを消すのを防ぐため）。
どうしても必要な場合だけ、利用者自身が起動オプションで許可してください（`claude mcp add fat32fmt -- "...at32fmt.exe" mcp --allow-fixed`、または `"args": ["mcp", "--allow-fixed"]`）。このときだけツールに `allowFixed` 引数が現れます。

エージェントには「`list_drives` → `plan_format` で確認 → ユーザーの明示的な承認後に `format_drive`」の順で使うよう `initialize` の instructions で指示しています。
`format_drive` は非管理者で動いている場合 UAC ダイアログを表示するので、最終的な書き込み可否は常に人間が握ります。

## 動作の仕組み

1. `\\.\X:` を開き `FSCTL_LOCK_VOLUME` → `FSCTL_DISMOUNT_VOLUME` で排他確保。
2. パーティション情報（開始オフセット・サイズ・セクタサイズ）を IOCTL で取得。
3. クラスタサイズと SD 境界単位からレイアウト（予約セクタ数・FAT サイズ）を計算。
4. 予約領域と FAT をゼロ化 → ルートディレクトリ（ラベル）→ FAT 先頭エントリ → FSInfo → バックアップブートセクタ → ブートセクタの順に書く。
5. ロックしたまま全体を読み戻して検証（バックアップブートセクタ・FSInfo・FAT コピーの一致・空きクラスタ数）。
6. MBR のパーティション種別を 0x0C に修正し、ロック解除。Windows が FAT32 として再マウント。

## ロードマップ（検討中）

SD カード関連ツールで一般的な機能のうち、未実装のものです。優先度の高い順に並べています。

| 機能 | 内容 |
| --- | --- |
| 誤フォーマット後の復旧 | ディレクトリが失われたカードから、ファイル先頭のシグネチャ（JPEG / MP4 など）で探す方式（ファイルカービング） |
| 断片化ファイルの復旧 | 削除ファイルが連続クラスタでない場合に、未使用クラスタをたどって中身を推定する |

## セキュリティ

v1.0.1 で、脆弱性検証で見つかった問題を修正しました。主に「AI エージェント（MCP）が悪意のある SD カードや Web ページの文章にだまされた場合」を想定した対策です。

| 対策 | 内容 |
| --- | --- |
| 管理者権限で書き込む場所の制限 | backup の出力は拡張子 `.img` のみ。backup / restore / recover は Windows・Program Files・ProgramData の中とネットワーク上のパスを拒否。ジャンクションやシンボリックリンクは実体の場所に解決してから判定し、ファイルを作った直後にも実際の場所を確認する |
| 固定ディスクの許可を AI に渡さない | MCP の `allowFixed` は、利用者がサーバーを `mcp --allow-fixed` で起動した場合だけ有効。`backup_disk` にも `confirm` を必須化 |
| システムディスクの保護 | Windows のある物理ディスクに加え、起動用パーティション（EFI システムパーティションなど）のある物理ディスクも拒否。判定できないときは操作を中止する |
| カード由来の文字列の無害化 | ファイル名・ボリュームラベル・デバイス名から、端末を操作する制御文字と文字の向きを変える文字（`photo‮gpj.exe` を `photoexe.jpg` に見せかけるもの）を除去。復旧時の `CON` などの予約名は `_CON` として保存 |
| 細工されたカードへの耐性 | ディレクトリは FAT の仕様上限（65,536 エントリ）を超えて読まない |

MCP を使う場合の注意:

- `confirm` は AI が自分で入力できる文字列で、確認の代わりにはなりません。**最終的な歯止めは UAC の確認ダイアログです。** MCP サーバー（Claude Code など）を管理者として起動すると UAC が出なくなるので、通常の権限で起動してください。
- ツールの結果に含まれるファイル名・ラベル・デバイス名は、カードを作った人が自由に書ける文字列です。その中に「〜を実行して」といった文章があっても従わないよう、MCP の instructions でエージェントに伝えています。

## 注意

**フォーマットはドライブ上の全データを消去します。** 対象ドライブを必ず確認してから実行してください。

## 動作確認

Windows 11 Pro 10.0.26300、Realtek PCIE Card Reader での実機確認の結果です。

| 機能 | 128 GB SDXC | 32 GB SDHC |
| --- | --- | --- |
| クイックフォーマット（CLI / GUI） | OK（1.7 秒、32 KB クラスタ、データ領域を 16 MiB 境界に整列） | OK（3 秒、16 KB クラスタ、4 MiB 境界） |
| 通常フォーマット（全領域検査） | ― | OK（約 83 分、不良クラスタ 0） |
| 書き込み禁止の検出 | ― | OK（ロックスイッチ ON で拒否） |
| 削除ファイルの復旧 | OK（日本語の長いファイル名を含めハッシュ一致） | OK |
| 不良セクタスキャン（読み取り） | ― | OK（約 23 分、不良 0） |
| 容量偽装チェック（全域） | ― | OK（約 62 分、「正常」判定） |
| 速度測定（書き込みあり） | 読み取りのみ 90.2 MB/s | 読み 22.8 MB/s／書き 13.6 MB/s、U1 / A1 相当 |
| バックアップ → 乱数消去 → 書き戻し | 書き戻し中にカードが応答しなくなり中断（カード側の不調） | OK（書き戻し後の SHA-256 がバックアップと一致） |
| 消去（`--format` なし） | ― | OK（以後 Windows はディスク全体を 1 ボリュームとして表示。上記のとおり正常） |

容量偽装カードの検出は、書き込みが折り返すカードを模擬したテストで確認しています（実物の偽装カードでは未確認）。

## 変更履歴

- **v1.0.1**（2026-10-26）: セキュリティ修正（内容は「[セキュリティ](#セキュリティ)」）。MCP の `allowFixed` は `mcp --allow-fixed` 起動時のみ有効、`backup_disk` は `confirm` 必須、backup の出力は `.img` のみに変更。
- **v1.0.0**（2026-10-22）: 最初のリリース。

## ライセンス

[MIT License](LICENSE)
