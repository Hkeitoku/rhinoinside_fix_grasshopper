# RhinoInside.GHFix — Visual Studio 開発版

Revit の「Rhino.Inside」タブに Grasshopper 起動用パネルを追加するアドインです。Revit に読み込んで使います。元 ZIP の C# 起動処理は維持しています。

## 対象環境

- Visual Studio Community 2026：「.NET デスクトップ開発」ワークロード
- .NET SDK 10.0.403（同じ 10.0.4xx 系の新しいパッチも利用可能）
- Revit 2026.5（この PC：26.5.0.55 / .NET 10）
- Rhino 8 と、Revit に対応した Rhino.Inside.Revit
- ビルド構成：Debug / Release、プラットフォーム：x64

Revit API は `C:\Program Files\Autodesk\Revit 2026` の DLL を参照します。

## Visual Studio で使う

1. `RhinoInside.GHFix.sln` を Visual Studio で開きます。
2. 構成を `Debug` または `Release`、プラットフォームを `x64` にします。
3. 「ビルド」→「ソリューションのビルド」を実行します。

出力先は `src\bin\x64\Debug\net10.0-windows` または `src\bin\x64\Release\net10.0-windows` です。

```powershell
dotnet build .\RhinoInside.GHFix.sln -c Debug
dotnet build .\RhinoInside.GHFix.sln -c Release
```

Revit のインストール場所が異なる場合は、プロジェクトの `RevitInstallDir` を変更してください。SDK の別系列を使う場合は `global.json` も変更します。

## GitHub と同期する

接続先は `https://github.com/Hkeitoku/rhinoinside_fix_grasshopper.git`（origin）、作業ブランチは `main` です。
このフォルダーのソリューションを開くと、Visual Studio の「Git 変更」からコミット・プッシュできます。プッシュには、接続先リポジトリに書き込み権限のある GitHub アカウントを使用してください。
`bin` / `obj` / `.vs` などの作業ファイルは Git の管理対象から除外しています。

## Revit へ反映する

ビルド後、Revit をすべて終了してから、このフォルダーで実行します。既存 GHFix の DLL と登録ファイルをバックアップして置き換えます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -RevitYears 2026 -Configuration Release
```

デバッグする場合は `-Configuration Debug` を指定します。
反映先は `%APPDATA%\Autodesk\Revit\Addins\2026`、バックアップ先は `%LOCALAPPDATA%\RhinoInside.GHFix\Backups` です。

## 起動・デバッグ

1. 上記のインストールを済ませます。
2. Visual Studio の起動プロファイル `Revit 2026` から F5 を押すか、Revit を手動で起動します。F5 自体はアドインをインストールしません。
3. 空のプロジェクトで「Rhino.Inside」→「GHFix」→「Grasshopper 安全起動」を試します。
4. ブレークポイントには Debug 版を反映してください。必要に応じて「デバッグ」→「プロセスにアタッチ」で `Revit.exe` の .NET マネージドコードに接続します。

ソース変更後は Revit を終了し、再インストールしてから起動します。

## ログ・復元

```powershell
Get-Content -LiteralPath "$env:LOCALAPPDATA\RhinoInside.GHFix\GHFix.log" -Tail 40
```

`GH_CANVAS_VISIBLE` は表示確認、`GH_CANVAS_TIMEOUT` は編集キャンバスの表示待ち時間切れです。
直前の DLL と登録ファイルへ戻す場合は、Revit を終了して実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\rollback.ps1 -RevitYears 2026
```

## ボタンアイコン

- Grasshopper 安全起動：`assets\grasshopper-direct-open-32.svg`
- 環境診断：`assets\grasshopper-version-info-32.svg`

元の SVG と、表示用の透過 PNG（16px / 32px）を同梱しています。PNG は DLL に埋め込むため、Revit 側への画像ファイルの別途コピーは不要です。
SVG を更新した場合は `python .\tools\render_icons.py` で PNG を再生成し、ビルドしてください。再生成時のみ Python の Pillow / Playwright と Microsoft Edge が必要です。通常の Visual Studio ビルドでは不要です。

## 保存先と確認範囲

保存先は OneDrive の `00開発環境\AI開発環境\RhinoInside_GHFix` です。
この README のインストール・起動操作は利用時の手順です。今回の準備作業では既存 Revit アドインの置き換えは行っていません。Revit 内での Grasshopper 起動とデバッグ接続は未確認です。
