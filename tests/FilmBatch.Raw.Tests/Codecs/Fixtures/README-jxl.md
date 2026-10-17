// JPEG XL 測試向量來源（獨立編碼器，非自產自銷）：
//   - 編碼器：libjxl cjxl 0.11.1（C++ 原生，與解碼邊界同源但獨立二進位）
//   - 整數向量由 PGM P5（16-bit big-endian）經 cjxl -d 0 -e 1 產生；
//     浮點向量由手寫 PFM（float32）／EXR half（float16）經 cjxl -d 0 產生；
//     有損向量由 -d 1.0 產生，色彩標籤以 -x color_space=DisplayP3 或輸入 ICC 覆寫。
//   - 獨立驗證：djxl（同版本原生解碼器）整數輸出與 jxl-oxide 0.12.6
//    （RawDecoderTests 無關；驗證用 throwaway Rust 工具，見交接說明）逐樣本一致；
//     無失真整數向量滿足 round(f * max) == 儲存值（max = 2^bits - 1，codestream 自身位元深度）。
// 向量一覽（檔名即內容；皆為單幀、無 alpha、無動畫／預覽）：
//   jxl-gray16-lossless.jxl      16x16 單平面 16-bit 無失真，值 30000+i*257（高值含 32768 以上）
//   jxl-gray16-lossless.bare.jxl 同上之 bare codestream（自 container 抽取 jxlc 承載）
//   jxl-mono12in16-lossless.jxl  10x6 單平面，12-bit 範圍值存於 16-bit codestream（程式碼深度 vs IFD 深度）
//   jxl-rgb16-lossless.jxl       10x6 三平面 16-bit 無失真（含高值）
//   jxl-rgb16-lossless.bare.jxl  同上之 bare codestream
//   jxl-f16-lossless.jxl         8x4 三平面 half 無失真，含 2.5／1.8／-0.5（線性標籤）
//   jxl-f32-lossless.jxl         8x4 三平面 float32 無失真，含 1.6／0.1／-0.15
//   jxl-f16-lossy-linear.jxl     8x4 half 有損（VarDCT），線性 sRGB＋D65＋intensity 255：唯一可接受的有損
//   jxl-rgb16-lossy-srgb.jxl     10x6 16-bit 有損，sRGB 轉換曲線：拒絕（非線性）
//   jxl-rgb16-lossy-p3.jxl       10x6 有損，P3＋sRGB 轉換曲線：拒絕
//   jxl-gray-lossy.jxl           10x6 灰階有損（單平面 XYB）：拒絕
//   jxl-f16-lossy-hdr.jxl        8x4 half 有損，intensity target 10000 nits：拒絕（HDR 色調映射）
// 未覆蓋（編碼器無此選項；以上游單元測試對等覆蓋）：
//   - orientation != 1 的 codestream（cjxl 無此旗標；以 JpegXl.CheckHeader 直接單元測試拒絕分支，
//     對應上游 output_for／open 的純函式單元測試風格）
//   - 自訂主色（BT.2100／自訂 xy）的線性有損還原矩陣：以 CheckHeader＋primaries 還原單元測試覆蓋數學，
//     交接說明記載以 ICC 注入產生此類向量的重現步驟。
