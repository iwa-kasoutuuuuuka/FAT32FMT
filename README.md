# FAT32FMT

SDXC など **32 GB を超える SD カード／USB メモリを FAT32 でフォーマット**するための Windows 向けツールです。
Windows 標準のフォーマット機能は 32 GB を超えるボリュームを FAT32 にできませんが、FAT32 自体は 2 TB まで扱えます。
カメラ・カーナビ・ゲーム機・3D プリンタなど、exFAT 非対応で FAT32 を要求する機器向けです。

- **GUI アプリ**（`FAT32FMT.exe`）: ドライブを選んでボタン一つ。管理者権限で起動（UAC）。
- **CLI**（`fat32fmt.exe`）: スクリプトや AI エージェントから使う。JSON 出力対応。
- **MCP サーバー**（`fat32fmt.exe mcp`）: Claude Code / Antigravity などから直接ツールとして呼べる。

## 特徴

| 項目 | 内容 |
| --- | --- |
| 対応容量 | 約 33 MB 〜 2 TB（セクタ 512 B の場合） |
| クラスタサイズ | 自動（Windows 既定表。32 GB 超は 32 KB）または 512 B〜64 KB を指定 |
| SD 規格に沿った配置 | データ領域の開始を SD の境界単位 (BU: 4 / 16 / 32 MB) に整列し、書き込み性能と寿命を確保 |
| パーティション種別の修正 | exFAT 由来の MBR 種別 0x07 を FAT32 LBA (0x0C) に書き換え（機器の互換性向上） |
| クイック／通常 | クイックはシステム領域のみ初期化。通常は全領域を書き込み検査し不良クラスタを FAT に登録 |
| ラベル | 11 バイト以内。日本語（CP932）も可 |
| 安全装置 | リムーバブルのみ列挙（固定ディスクは明示的に許可が必要）、システムドライブおよびシステムと同じ物理ディスク上のドライブを拒否、書き込み禁止（ロックスイッチ）の検出、確認入力、UAC |
| 書き込み後の検証 | フォーマット直後にブートセクタ・バックアップ・FSInfo・FAT の全コピー・ルートディレクトリを読み戻して検査 |

## 構成

```
FAT32FMT/
├─ FAT32FMT.slnx
├─ src/
│  ├─ Fat32Fmt.Core/              コアライブラリ
│  │   ├─ IO/                     IBlockDevice とストリーム／サブデバイス実装
│  │   ├─ Fat32/                  レイアウト計算・ブートセクタ・FAT 書き込み・ボリューム読み取り
│  │   ├─ Diagnostics/            FAT32 構造の読み戻し検証 (Fat32Verifier)
│  │   └─ Windows/                Windows 固有: ボリュームのロック/マウント解除、ドライブ列挙、
│  │                              MBR 種別修正、UAC 昇格実行、フォーマットサービス
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

## GUI の使い方

1. `FAT32FMT.exe` を起動（UAC が出るので「はい」）。
2. ドライブを選ぶ（リムーバブルのみ表示。カードの抜き差しで一覧は自動更新。内蔵ディスクを対象にしたい場合のみ「固定ディスクも表示」）。
3. 必要ならクラスタサイズ・ラベルを変更。「対象の情報」にフォーマット後のレイアウトが表示されます。
4. 「フォーマット開始」→ 確認ダイアログで「はい」。

コマンドラインで初期値を渡せます: `FAT32FMT.exe E: --label MYCARD --cluster 32K`

## CLI の使い方

```powershell
fat32fmt list                      # フォーマット可能なドライブ一覧
fat32fmt plan E:                   # 書き込まずにレイアウトを表示
fat32fmt format E: --label MYCARD  # フォーマット（確認入力あり。非管理者なら UAC で昇格）
fat32fmt format E: --yes --json    # 確認なし・JSON 出力（自動化向け）
fat32fmt image disk.img --size 64G # イメージファイルを FAT32 化（テスト用。スパースファイルなので空き容量以上でも可）
fat32fmt image sd.img --size 128000000000 --mbr  # SD カードと同じ MBR + パーティション形式で作成
fat32fmt verify sd.img             # イメージの FAT32 構造を検査（異常があれば終了コード 1）
```

主なオプション: `--cluster 32K` `--label 名前` `--full`（全領域検査） `--no-ptype`（MBR 種別を変えない）
`--allow-fixed`（固定ディスク許可） `--align 16M`（整列境界） `--json` `--yes`

終了コード: 0=成功 1=エラー 2=使い方の誤り 3=キャンセル（UAC 拒否を含む）

管理者権限が無い場合、`format` は自分自身を UAC 昇格して起動し、結果を一時ファイル経由で受け取って表示します。
AI エージェントから呼ばれた場合も **人間が UAC で承認しなければ書き込みは始まりません**。

## Claude Code / Antigravity から操作する

### 方法 1: CLI をそのまま使う

エージェントにシェルから `fat32fmt list --json` → `fat32fmt plan E: --json` → `fat32fmt format E: --yes --json` を実行させるだけで動きます。

### 方法 2: MCP サーバーとして登録する

`fat32fmt.exe mcp` は stdio の MCP サーバーです。ツールは 3 つ:

| ツール | 内容 |
| --- | --- |
| `list_drives` | フォーマット可能なドライブ一覧（`includeFixed` で固定ディスクも） |
| `plan_format` | 書き込みなしの検証とレイアウト計算 |
| `format_drive` | フォーマット実行。`confirm` に `"FORMAT E"` のようにドライブ文字を含めた文字列が必須 |

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
| 容量偽装 (フェイクフラッシュ) チェック | 全域に書き込み、読み戻して照合する (H2testw / F3 相当) |
| 速度テスト | 読み書きの実測速度を計測し、SD のスピードクラス (Class 10 / V30 など) の目安を表示 |
| 不良セクタスキャン | フォーマットとは独立した読み取り専用の検査 |
| イメージのバックアップ／復元 | カード全体を `.img` として保存・書き戻し、書き込み後ベリファイ |
| セキュア消去 | 0 埋め・乱数埋めで復元不能にする |
| 削除データの復旧 | 誤フォーマット後のファイル復元 |

## 注意

**フォーマットはドライブ上の全データを消去します。** 対象ドライブを必ず確認してから実行してください。
Windows 11 Pro 10.0.26300 + 128 GB SDXC（Realtek カードリーダー）で動作確認済み。

## ライセンス

[MIT License](LICENSE)
