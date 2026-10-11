# HID Remote Remapper

Windows 10/11 x64用。`HIDRemoteRemapper.exe` と `remapper.ini` を同じフォルダーに置き、EXEを実行します。Windows標準の.NET Framework 4.xを利用し、追加のパッケージやドライバーは不要です。

## 使い方

1. リモコンを接続し、EXEを起動。通知領域へ最小化した状態で起動します。
2. Consumer変換は初期状態でON。電源→X、青→F1、赤→F2、緑→F3、黄→F4、方向→キーボードの方向キー、決定→Enter、戻る→Esc、メニュー→P。
3. キーボード変換も初期状態でON。上段のF5→D、F1→R、F2→Sを変換します。「Fキー変換 ON（全キーボード対象）」で切り替えできます。
4. INIを編集したら「INI再読込」。入力ログでConsumerのUsage値と、リモコンのキーボード入力を確認できます。
5. 最小化すると通知領域へ格納。アイコンをダブルクリックで表示、右クリック→終了で終了します。

変更しないFキー、数字、SEARCH、HOME、TV_PROGRAMS、チャンネル、音量は元の入力をそのまま利用します。Windowsや利用先アプリが元々対応していない入力については、このアプリでも追加の動作は発生しません。

## INI

`[Consumer]` は16進Usage ID→Windowsキー名。例: `0030=P`、`006B=F1`。`[Keyboard]` はWindowsキー名→Windowsキー名。例: `F5=D`。キー名は `Enter`, `Escape`, `Up`, `Down`, `Left`, `Right`, `Home`, `F1`〜`F24`, `A`〜`Z`, 数字は `D0`〜`D9`。未登録の入力は変更しません。設定エラー時には直前の設定を保持します。

対象は参照チャットのVID 06E7 / PID 8099。Consumer Report 02の9バイト（ID+16ビットUsage×4）を処理します。色とメニューのUsageはUSB HID規格に基づく初期値、戻るはAC Back 0224およびMenu Escape 0046です。ログに違う値が出る場合はINIを変更してください。キーボード形式の方向キーなどはそのまま通ります。

Consumer入力は長押しでリピートします。`[Repeat]` の `DelayMs=500` は最初の待ち時間、`IntervalMs=30` はその後の間隔（ミリ秒）です。編集後は「INI再読込」。ボタン解放、Consumer変換OFF、INI再読込、デバイス切断、終了でリピートを停止しキーを解放します。Windowsのタイマーによるため間隔には多少の誤差があります。

## 制限

- Consumerの対象デバイスはVID/PIDで限定します。一方、通常のWindowsキーボードフックではデバイスを識別できないため、Fキー変換を有効にすると普通のキーボードのF1/F5も変わります。初期状態ではONです。リモコンのキーボード入力だけを抑止・置換するには、別途デバイス単位のフィルタードライバー等が必要です。
- Raw Inputは元のConsumer入力を抑止しません。Windowsやアプリが元の入力にも反応すると二重動作になります。特に電源ボタンは、実際の電源動作がある環境ではWindows側の設定も確認してください。
- Consumerの変換は押下・解放と長押しの自動リピートを送ります。キーボードFキー変換は元のリピートを引き継ぎます。
- 管理者権限のアプリに入力する場合、Windowsの制約により本アプリも同等の権限で実行する必要があります。
- 実機のリモコンでの検証は未実施です。コンパイル、レポート解析、解放レポート、キー名、Win32構造体サイズを自動確認済み。

## 入力が出ない場合

起動時にRaw Input登録結果と接続デバイス一覧を画面のログに表示します。通知領域のアイコンをダブルクリックして表示し、「デバイス再検出」で更新できます。「対象コレクション 0件」の場合は、対象リモコンがWindowsに認識されているか、INIのVID/PIDが正しいかを確認してください。USB・Bluetoothのパス形式とHIDの数値VID/PIDに対応しています。入力を受け取ったデバイスは「受信元」として表示し、対象外のデバイスについてはボタン内容を記録しません。

ログファイルは既定で出力しません。`HIDRemoteRemapper.exe --log` で起動したときだけ、EXEの隣の `diagnostic.log` に追記し、起動時の例外を `crash.log` に保存します。ショートカットのリンク先末尾に ` --log` を追加しても使えます。フラグを変更する場合は一度終了してから起動してください。過去のログファイルはそのまま残します。

## ビルド・検証

PowerShellで `./build.ps1`。ソースは `Program.cs`。`HIDRemoteRemapper.exe --self-test` はキーボード入力を送らずに基本検証を行い、結果を `self-test.txt` に保存します。

参考: [Microsoft Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input)、[USB HID Usage Tables](https://www.usb.org/sites/default/files/hut1_21_0.pdf)。

## ライセンス

[MIT License](LICENSE)
