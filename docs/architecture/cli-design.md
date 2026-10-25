# 架構：Cli 命令列工具

本文件是[架構說明書](../architecture.md)「4. 模組說明」的一部分，涵蓋 4.11：命令列工具的程式結構。

## 4.11 Cli．命令列工具

`src/FilmBatch.Cli/Program.cs` 以 System.CommandLine 定義 `export`（套用已保存的專案設定）、`samples`（產生測試照片）、`gpu-info`、`spike`（GPU 基準測試）及 `raw convert`／`raw masks`。CLI 與 GUI 共用 Core／Engine；`export --format dng` 是算圖後的 DNG，`raw convert` 則保留感光樣本並補足來源色彩描述，兩者不能混為一談。用法見 [building.md](../cli.md#命令列工具cli)，隔離驗證見 [VERIFY.md](../../VERIFY.md)。
