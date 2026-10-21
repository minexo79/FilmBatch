# AI 物件移除：模型、畫質與操作 Survey

- 查核日期：2026-10-21；各節保留原實驗日期與資料邊界。用途：**研究／比較與實作選型提案，不是已支援功能**。
- 結論：把「選取」與「重建」分開；**目前不採用任何候選作為通用物件移除的產品預設，T1 維持 NO-GO**。MI-GAN 保留快速工程對照，EfficientSAM 僅作待人工修正的選取提案，Moebius 暫緩；沒有量測出「最佳畫質」模型。既有[續驗決策](#10-續驗與採用決策)、[來源續查](#11-權重來源與後續研究)、[場景微調／VAE 內容實驗](#12-場景微調vae-身分與原生續驗)、[替代方案](#13-多角色替代方案與實驗)與[小型模型／Depth](#14-小型模型與-depth-分層實驗)仍成立；[提示詞／LLM 引導實驗](#15-提示詞與-llm-引導實驗)不把語意控制當成移除品質或產品資格。
- 已做：模型／權重來源、完整鏈、VAE、替代方案、OpenCV／.NET donor、小型 artifact 精度、Depth Small 可見圖層研究、真正文字條件模型與本機 Qwen 提示詞對照、Mac 公開 API、四個既有 CLI，以及訓練空間條件分支的留存輸出比較。未做：40 場景人類盲評、Windows／iOS 移除推論、直接付費影像 API、私人照片上傳或產品功能實作。**第 13 節記錄研究期間整個 `dist/` 消失及 v2 重新凍結資料的邊界；後續實驗沿用 v2，不補出已消失的舊收據。**
- 配套：[ADR 0005](adr/0005-ai-object-removal.md) 記錄取捨；[操作與資料契約](object-removal-design.md) 固定 UX／色彩與保存邊界；[實作計畫](superpowers/plans/2026-10-20-ai-object-removal.md) 提供 T1–T5 與 OR-01–OR-14 對照。

最新延伸：[原生平台與最低可行成本](#16-原生平台與最低可行成本)、[既有 CLI 實測](#17-既有-cli-作為外部模型入口)與[訓練空間條件續驗](#18-訓練空間條件續驗與最小可行路線) 將公開系統 API、外掛權重與外部來源放在相同產品／裝置條件下比較；不是要求使用者換硬體，也不把作業系統 app 的功能當成可嵌入 SDK。

最新決策整理：[第 19 節情境解法／成本效益](#19-情境解法成本效益與決策紀錄) 記錄 41 項候選想法、11 個情境及各自 No-Go／重開條件；[兩案資格驗證計畫](superpowers/plans/2026-10-21-object-removal-scenario-qualification.md) 只規劃下一輪必要條件篩選，不宣稱已證明最高效益或通過產品 gate。

## 1. 問題要拆對：選哪裡，與補什麼

| 路線 | 能力／適用場景 | 不能保證的事 | 對 FilmBatch 的定位 |
|---|---|---|---|
| 傳統 patch／texture synthesis，例如 PatchMatch | 尋找與搬移相似紋理，可提供來源區控制；無模型權重 | 大洞、透視、被遮蔽語意與因果影響不會因有相似 patch 就自動解決；也可能複製出重複物件 | 非 AI 對照基準；若後續提供 Clone／傳統移除，須如實命名，不能作 AI 失敗後的隱藏替代 |
| 非 diffusion 的學習式 inpainting，例如 MI-GAN／LaMa | 依可見上下文合成背景；可作本機固定成本候選 | 沒有文字提示不表示不會生成新物件；大洞、週期紋理、線條或高頻細節仍可能失敗 | 優先工程基準，不預先授予畫質冠軍 |
| diffusion／專用生成式 inpainting，例如 Moebius、FLUX Fill、ObjectClear | 可能利用較強影像先驗完成複雜結構；ObjectClear 特別針對物件及影響移除 | 隱藏場景沒有唯一真值；可能殘留、扭曲、產生新干擾物，或改到不該動的區域 | 與本機基準同場比較；完整 runtime、授權與資源合格才採用，不依模型大小或論文名次決定 |
| SAM 類 promptable segmentation | 點／框引導物件範圍，再由人加減修正 | 不是填補，也不保證理解陰影、反射、細線、半透明物或物件因果關係 | 選取輔助；手動筆刷／圈選仍是完整基本路徑 |

傳統／學習式／diffusion 是方法分類，**不是品質保證階梯**。消除可見物件、補得合理與還原真實被遮蔽內容是三種不同主張。來源 S01–S08 支持方法與候選描述，沒有本機比較結果。

## 2. 填補候選與採用資格

以下表格的架構風險依原始碼作 **[INFERENCE]**，作者畫質宣稱不是獨立 benchmark。第 9 節另列已實跑的 macOS artifact／數值與限制；FilmBatch 產品整合、Windows DirectML 與 iOS 移除推論仍為 **not-run**，不能從本機子圖外推。

| 候選 | 已查到的來源／權利 | 工程風險與畫質未知 | 決定 |
|---|---|---|---|
| **MI-GAN-512 核心** | S02 的 MIT 程式碼及獨立 `LICENSE-WEIGHTS`；官方 README／exporter 區分核心與便利 pipeline | FP32 原生數值通過不代表移除成功；續驗大洞生成新彩色形狀，真實目標區域亦出現不相關形狀。只驗固定 512 輸入 | **保留快速工程對照，不採用為通用移除預設**；不把較小洞的少量結果改稱已合格的產品子功能 |
| **LaMa／big-lama** | S03 程式碼 Apache-2.0；檢視的第一方位置仍未建立 exact checkpoint 權利，issue #368 無回覆 | FFT／exporter／精度與 fallback 仍是未實跑的 artifact 問題；不能由授權卡關推論畫質或工程失敗 | **暫停下載與採用**，取得具名權重的第一方條款或合格 owner 裁決後重議 |
| **Moebius（ECCV 2026）** | S05 的 code／weights Apache-2.0 明文與 S06 的 MIT metadata 差異保留；原始官方 VAE 不等價，S23 FP16-Fix 轉 FP16 後則與現有 payload 內容一致，見第 11／12 節 | 既有 .NET 完整鏈的 strict fail 與高成本保留；新 `ft_places2` 的同輸入參考仍有碎片／新白色形體，原生證據按 artifact／平台分開記錄 | **暫緩工程主線及產品採用**；不將單一 checkpoint 的反例外推整個家族，也不以來源內容比對取代畫質或散布 gate |
| **ObjectClear（CVPR 2026）** | S07 的 NTU S-Lab 1.0 含非商業用途限制；完整 base／checkpoint 權利仍須分別追蹤 | 專門處理物件及其影響，適合作陰影／倒影評估參考；SDXL 類完整鏈的資源與原生轉換成本未測 | **品質研究參考，不列一般產品預設**；適用用途、下載或執行須先取得相應資格 |
| **FLUX.1-Fill-dev** | S08：模型使用的 NC 條款與輸出的使用條款不同；開放程式碼不代表模型可自由商用 | 大型 denoiser 以外還有 VAE／文字元件與採樣成本；量化、offload 可能改變品質／速度。未在本機執行，不能宣稱只支援某類硬體 | **有條件品質參考，不內建或呼叫**；有合法執行權利且測得必要品質優勢時才重議 |
| **MAT** | S04 的非商業／研究用途限制及 StyleGAN 系相依需分別審查 | 參考實作的自訂算子增加原生轉換成本；不把 CUDA 程式碼存在等同數學上不可轉換 | 不列初次短名單；授權或替代算子取得明確資格時才重議 |
| **AOT-GAN／LaMa-Dilated、ZITS 類、PowerPaint／OmniPaint、BrushNet** | 候補線索；第 11 節補查 PowerPaint v2.1、BrushNet 與 AOT-GAN 的具名來源，尚未閉合完整權利鏈 | 架構／模型家族名不能繼承其他 artifact 的品質或平台資格；額外網路、base 模型與轉換器都必須算入成本 | 不作已合格產品候選；先釐清現有 checkpoint／VAE，再決定是否值得引入另一套完整模型鏈 |

### MI-GAN 的 8-bit 邊界不能忽略

S02 的官方 `migan_pipeline_v2.onnx` 是 **UINT8 RGB＋UINT8 mask 進出**，其中 mask 255 表示已知區域；wrapper 包含 crop／resize／normalize／blend 等處理。不能把它的任意尺寸介面說成核心本身支援任意尺寸，也不能把核心的浮點介面當成完整 pipeline 的介面。

產品候選採核心浮點模型與 FilmBatch 自己的高精度合成，**只轉換模型所需 ROI，不把整張 RGB16／RAW 經 UINT8 round trip**。模型孔洞 polarity、channel 順序與 range 必須從實際 exporter／artifact 確認；不同家族不共用一個猜測的前處理。保存為 RGB16 只是避免再量化，不會憑空增加模型細節。

### LaMa 的 Fourier／精度是實驗問題

原始 FFC 程式碼足以證明存在 FFT，不足以證明某個 ORT 版本／exporter 已將它如何 lowering、會切成幾個 partition，或傳輸成本多大。固定尺寸的 MatMul 轉換可以研究，但會改變尺寸約束與計算成本；不把「標準 ONNX 算子」直接翻譯為全圖加速成功。局部權重或 normalization 可能不適合直接 FP16；本輪未逐張量測，故不記錄未複核的 overflow 數值或通用安全宣稱。

### 授權鏈與取得方式

逐項列 **程式碼、權重、base／adapter、VAE／文字元件、exporter、runtime、訓練／蒸餾資料來源**。MI-GAN 的 MIT 權重文字與 Places／FFHQ／teacher 來源是不同事實；訓練資料限制是否以及如何約束衍生權重，由適當 owner／法律審查判定，這份 Survey 不作肯定或否定的法律裁定。

Moebius repo 的 Apache-2.0 與模型卡 MIT 必須並列，採用時重新檢查選定 bytes 與 VAE 條款；兩者均屬寬鬆標籤也不能無聲合併成一個來源。未知權重授權、adapter 掛 MIT 而 base 另有限制，均不能靠「另外下載」或「使用者匯入」解除。Windows／Apple 現有 ORT 版本與 notices 做法沿用 [ADR 0002](adr/0002-ai-denoise.md)，本輪沒有安裝新相依套件。

## 3. 選取輔助短名單

| 候選 | 查核到的實際供應面 | 選擇與限制 |
|---|---|---|
| **EfficientSAM-Ti** | S10 repo Apache-2.0，export script 有完整與 split encoder／decoder 路徑 | 優先可攜候選；目前照片 embedding 可快取，提示變動只重算 decoder。實際權重 digest、圖與數值仍須驗證，不只下載 decoder 就宣稱完成 |
| **SAM 2.1** | S09 README 明列程式碼與 checkpoint Apache-2.0 | 分割品質比較候選；原作者 PyTorch／GPU 數字不證明 C# ORT／CoreML／DirectML／iOS 部署 |
| **MobileSAM** | S11 的 advertised ONNX script 以 `image_embeddings` 為輸入，處理 prompt／mask decoder | 可研究，但不是完整影像 encoder 原生部署證據；不能只因名稱有 Mobile 就優先承諾手機 |
| **SAM 3** | S12 固定版為自訂 SAM License，不是自動繼承 SAM 2 的 Apache-2.0 | 額外使用／條款審查；初次不增加文字概念搜尋與整圖庫語意索引，也不為「更新」而加入 |

手動筆刷、閉合圈選、加選／減選必須能完成流程。選取模型失敗時可以清楚停用輔助、保留人工選取；**填補模型失敗時不可以回傳原圖假成功**。兩者不是相同的 fallback 語意。

## 4. 操作參考與 FilmBatch 的取捨

### 原廠文件提供的線索，不是可嵌入 API 的證據

- **Lightroom Classic／mobile（S13／S14）**：把產生、加減選、檢查遮罩、更新結果／變體與接受分開；mobile 文件另有解析度／速度選項。Classic 的物件偵測可涵蓋陰影／反射，但這是該產品路徑的說明，不是純筆刷或任意 SAM 的保證。
- **UX 子評估讀到的補充（S15／S16）**：Photoshop 的筆刷／圈選及逐筆／累積操作、Apple Photos 的點／刷／圈與完成動作，可作互動參考。協調者未另行逐頁複核這兩頁，故不以它們裁定裝置、離線、授權、模型身分或可嵌入能力；本輪也未實際操作這些產品。
- **雲端 API（S17）**：公開文件有明確物件移除介面，不表示 Photoshop／Lightroom 的產品 backend 可直接供 FilmBatch 嵌入，也不保證未來服務、區域、帳務與資料政策不變。雲端不是本輪建議的自動後備；若之後考慮，需重新查核實際 API 契約與法律／付費資格。

### 建議的一個工具

1. 選焦點照片 → 進「移除」→ 筆刷／圈選或按「協助選取」→ 加減修正遮罩。
2. 明示陰影／倒影與細線可能需補選；不要把「一鍵」變成無條件產品承諾。
3. 按「產生」，顯示實際 ROI、模型與進度。畫面可平移／縮放；取消會解除忙碌，不提交部分結果。
4. 在相同正式基底上比較「修補前／候選」，100% 檢查接縫、重複紋理與新干擾物。確定性模型可只有一個候選，不偽造三個變體。
5. 「套用」才寫一筆可 undo／redo 的照片命令，保存結果後可關閉重開／無模型匯出。
6. 上游曝光／WB／降噪改變時顯示內容過期，可重新生成或明確預覽「保留像素、重套用」；來源／幾何改變則硬過期，不能猜測原遮罩仍對得上。改前序修補保守使整個後序過期。

「原圖」與「修補前」必須不同名：前者是既有來源比較，後者保持同樣現像／裁切／效果、只排除修補。桌面需鍵盤可操作、行動需可到達的套用／取消與兩指平移；沒有 Pencil 壓力也能完整使用。批次以逐張 badge、過期數量、逐張接受與取消恢復，不能匯出時偷偷重新推論。

## 5. 影像與專案整合決策

現行來源是 `SourceImage`：一般影像為 straight RGB16 sRGB，RAW 另外保留不可變、未夾取的線性 Rec.2020 `Scene`。既有 RAW 語意遮罩供局部調整，不是可編輯移除遮罩。`CpuPipeline` 的 `BasicDisplay → ToneSplit → Detail → Dirt → Grain` 提供可辨識的顯示域邊界。

**[INFERENCE／設計提案]** 在 `ToneSplit` 後、`Detail` 前合成：模型看到 pre-detail 顯示畫面，新區域和周圍一同接受後段清晰／銳化與顆粒。這不保證模型生成的紋理原本夠銳利；強 clarity 仍是必測反例。完全透明區域不作選取，RGB 模型不處理 alpha；細節見設計文件。

有修補使用 canonical CPU 完整解析度基底／結果，預覽及縮圖由它縮小；沒有修補保留既有 GPU 快速路徑。**完整尺寸合成不是原生完整尺寸生成**：模型若只推論 512 ROI，必須保留真實尺寸標示。大圖現像、buffer 與模型 activation 的總成本可能令低記憶體裝置不合格，不能以小權重推論能放進 iOS。

使用 `PhotoEntry` 的不可變逐張 `RetouchStack` 與專案自有 bbox patch／mask／筆劃資產，不放進可貼到其他照片的 `Settings`／preset。先資產後 revision-checked JSON，undo／redo 重播像素，不重新推論；搬動完整專案仍可讀。缺模型不阻擋重播，缺資產／錯基底則逐張錯誤。精確遮罩外不變限於合成點；最終影響區須計入後續所有空間濾波支撐，包括顆粒柔焦，不只一個固定銳化半徑。

## 6. 固定的畫質與 UX 評估方案

以下為**建議的實作驗收方案，尚未執行**。先固定資料、mask、判準與正式處理域，再調參；不得讓模型作者圖或事後挑選成功案例成為驗收集。門檻是本次產品提案，不是學界標準或既有使用者承諾。

### 資料與比較分離

- 固定 **40 個授權清楚的場景**：下列 8 類各 5 張；每類預先指定 2 張開發比較、3 張留出驗收，即 T1 用 16 張、T5 用另外 24 張。manifest 保留來源／使用權、來源 SHA-256、原始尺寸、RAW／一般影像與 mask 身分，不使用未經同意的私人照片。
- 場景分層：① 重複／自然紋理；② 建築直線／地平線／文字；③ 人物相鄰／髮絲／臉緣；④ 影子／倒影／接觸面；⑤ 大洞（確認 mask 占畫框 >30%）；⑥ 電線／欄杆／細線；⑦ 畫框邊緣／透明邊界；⑧ 高 ISO／RAW 高光與強 clarity。複數標籤照實保留，不用單一分類隱藏難例。
- 先以**同一人工確認 mask、同一 canonical 輸入**比較填補。各模型使用自己必要且記錄的 resize／padding／context，不把不同正式 mask 的差異歸因為填補品質。再比較「人工」與「人工＋輔助」整體工作。
- 合法可取得的 MI-GAN、LaMa、Moebius 同場；傳統 patch 為非 AI baseline，選用既有合法實作或外部工具，不在本產品擴寫第二個填補引擎。ObjectClear／FLUX 只有取得相應執行權利才加入；未取得列 excluded＋原因，不能冒稱擊敗它們。
- 合成遮蔽的乾淨參考圖可量測 PSNR／SSIM／感知距離；真實移除通常沒有唯一背景真值，不能把參考距離當美觀或物件真正消失的證明。重建與隨機生成分開報告，固定 seed／變體選擇政策，不只挑最好 seed。

### 盲評與硬失敗

| 面向 | 記錄與判準 |
|---|---|
| 物件及影響是否移除 | 殘留、陰影／倒影、顯眼新物件各列失敗，不能只評「背景漂亮」 |
| 結構／紋理／接縫 | 直線、透視、文字、重複圖樣、色調、噪聲與細節連續性；同看整圖和 100% ROI |
| 受保護內容 | 非目標臉／人體／文字明顯扭曲為該例失敗；未獲授權區域越界亦失敗 |
| 像素契約 | 同 CPU 合成平面 `M=0` 精確保留基底與 alpha；最終輸出只允許實際後段濾波支撐內的影響，不放寬既有容差 |
| 可用性／失敗 | 缺模型、拒絕、OOM、崩潰、逾時與取消分列並保留全部嘗試；不可從成功案例均分中消失 |
| 總工作 | 選取時間、修正筆劃、變體／重試次數、等待、取消、過期恢復時間；畫質比較不得只看模型核心毫秒 |

至少 **3 位評審**，隨機隱去模型名／呈現順序，各自給「移除完整、結構、接縫／紋理、無新干擾物」0／1／2 分（不可用／明顯需修／可接受），另給同場成對偏好，可平手；保留分歧和最差例。評審先獨立作答，再討論，不能由生成或選型的同一 agent 自己充當三位人類。

一例通過：沒有硬失敗，且多數評審對四項均給 2。候選最低產品 gate：留出集至少 `ceil(0.8 × 有效預先固定案例數)` 通過，各分層至少 60%，所有像素／原檔／離線保存硬契約通過。有效資料因授權等退出須在執行前替補並固定；模型拒絕／逾時仍算失敗，不從分母移除。未達標只能縮窄**明示且經 owner 同意**的支援場景或拒絕候選，不能事後調低門檻。

通過 gate 後依留出集成對偏好與每類失敗選型，總時間／記憶體作取捨；平手或資料不足如實保留。這是有限樣本上的產品決定，**不證明所有攝影、所有尺寸或整個市場的最高品質**。

### 平台與可重現性

每個 artifact／裝置記錄完整 manifest、權重及 exporter SHA-256／revision、opset、張量 dtype／shape／range／polarity、色彩域、預處理與 crop-back、真實 provider／partition／fallback、冷啟動、暖推論 p50／p95、完整現像／ROI／合成時間及峰值程序記憶體。延遲分布需記錄樣本數與冷暖定義，不混成一個數字。

互動目標：筆刷 frame p95 ≤33 ms、取消動作 UI 回應 ≤100 ms；native kernel 真正返回時間另外量測，不能靠提早隱藏 progress 假裝已停止。取消後不能發布／快取結果，之後可再明確生成。macOS、Windows、iOS 的 compile／native link／Simulator／真機分欄；CPU 真實推論可以合格，混合 provider 必須如實報告。iOS 只有實機品質／記憶體／生命週期過關才宣稱產生支援；已接受資產重播也須獨立驗證。

## 7. 平行角色、反方與採納紀錄

三個真正平行的子任務：`LocalInpaintingLens`（本機模型／算子與供應）、`GenerativeRemovalLens`（新模型／完整生成鏈／權利）、`RemovalUXLens`（攝影流程／反例與產品驗收）。角色名不是不同模型供應商的證據；共同背景與工具也不構成獨立 benchmark。以下為協調者整合後的**文件決策**，不是執行或法律核准。

| ID | 問題／最強反方 | 狀態與本輪處置 | 證據／可推翻條件 |
|---|---|---|---|
| R1 | 易部署的 MI-GAN／LaMa 是否等於畫質最佳？ | 採納比較，不採納冠軍宣稱；加入 Moebius 同場 | 原作者來源 S02／S03／S05＋T1／T5 人類盲評；第 9 節只有可行性實測，沒有畫質盲評 |
| R2 | 沒有 FFT／文字提示就不會生成新物件？ | 否決；所有神經填補都要驗殘留、新物件與結構 | 「無新干擾物」是品質硬失敗，不由架構名豁免 |
| R3 | 標準算子／小權重證明原生全加速或 iOS fit？ | 否決；允許真實 CPU／混合 provider，但要求完整圖與實機成本 | T1 artifact 執行與 T5 平台矩陣；未跑仍 not-run |
| R4 | 把模型另外下載能解除資料／權重限制？ | 否決；來源鏈與明確權重條款分開記錄，不作法律結論 | S02–S08；採用前 owner／法律審查 |
| R5 | 顯示域 patch 放在 detail 後會形成未銳化島；是否改成整張現像副本？ | 部分採納：改成 pre-detail 插入，拒絕另外換來源；保留模型本身可能軟化的風險 | 已讀 CPU 順序；T5 強 clarity 反例；若仍有系統性接縫再重議 |
| R6 | 曝光小改也必須全部重推論，工作成本是否不可接受？ | 採納兩級過期＋明確可復原 rebase；幾何／來源不能 rebase | T2 keys／undo，T4 批次恢復；不無聲匯出舊基底 |
| R7 | 只失效相交的後序 mask 是否安全？ | 否決，UX lens 接受修正；完整後序失效 | 全域／ROI halo context 可能讀遠方前序 patch；T2 non-overlapping suffix 反例 |
| R8 | 最終畫面遮罩外一定不變，grain 都是局部？ | 修正，UX lens 接受；嚴格合成點＋實際後段支撐 | 現行 `GrainSoftBlur`；T5 逐參數推導與像素檢查 |
| R9 | 用 SAM／廠商一鍵 UX 是否可免除手工修正？ | 否決；加減選、影子／反射補選、接受／拒絕仍須存在 | S09–S14；T4 實際操作，不由模型評審宣告 UX 成功 |
| R10 | 只存 seed、匯出時再生能省磁碟？ | 否決；保存接受像素／mask／筆劃、原子提交與資產保留 | 既有不可變命令與 revision-checked 保存；T2／T3 無模型重開及錯資產失敗 |
| R11 | 應直接使用大型模型／雲端以追求品質？ | 延後採用、不排除比較；權利、外送／付費與完整資源先過獨立 gate | 若合法同場評估顯示有必要的明顯優勢，owner 再決策；目前無雲端 fallback |

**Socratic 問題與判斷**：背景「像真的」是否等於原物件與影響都消失？不等於，分項驗收。移除一張容易的照片是否代表整批可用？不代表，列全分母與最差類別。使用者已接受的結果是否可以在重開／匯出時換掉？不可以，故保存像素。模型較新或更大是否必然較好？不必然，故固定輸入盲評。可編譯是否代表手機可用？不代表，故實機與完整資源 gate。

角色最終意見：本機 lens 同意有條件的工程候選；生成式 lens 為 **AGREE WITH CHANGES**（完整權利鏈、作者與本機證據分開）；UX lens 在 pre-detail、rebase、整個後序失效及 grain 支撐修正後無其他反對。**共識僅足以建議 study／reproduce，尚不足 adopt／deploy**。來源權利、移除畫質、完整 pipeline 成本與實機資格仍未解；不能由三票同意變成已驗證功能。

## 8. 來源帳本與證據邊界

科學證據讀取層級：以原作者 repo／README／實作／授權及論文書目為主，**沒有完整重現論文 benchmark，也未對論文表格作獨立資料稽核**。`verified source text` 只證明來源確實如此寫，不證明作者量測或跨平台外推成立。下列固定 pin 指 repo 原始碼，不是模型檔 SHA-256；正式採用還要固定取得的 artifact。

| ID | 已讀材料與來源 | 查核／固定點與仍未知事項 |
|---|---|---|
| S01 | [PatchMatch 原論文書目](https://doi.org/10.1145/1531326.1531330)；[原作者投影片的 Princeton 課程鏡像](https://www.cs.princeton.edu/courses/archive/fall18/cos526/notes/patchmatch.pdf) | 論文站全文 HTTP 403；實讀投影片解釋 randomized correspondence／patch hole filling，不把投影片當本機重現或可重用程式授權 |
| S02 | [MI-GAN README](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/README.md)；[權重 MIT](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/LICENSE-WEIGHTS)；[pipeline exporter](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/scripts/create_onnx_pipeline.py) | pin `2b793c5ece43f4253e32d4afc257120a5deed6f5`；已取得原 checkpoint 並匯出 FP32 核心，macOS 原生結果見第 9 節。另下載的 `migan.onnx` 實查仍是 UINT8 pipeline，不是浮點核心 |
| S03 | [LaMa README](https://github.com/advimman/lama/blob/786f5936b27fb3dacd2b1ad799e4de968ea697e7/README.md)；[FFT 實作](https://github.com/advimman/lama/blob/786f5936b27fb3dacd2b1ad799e4de968ea697e7/saicinpainting/training/modules/ffc.py)；[權重授權問題 #368](https://github.com/advimman/lama/issues/368) | pin `786f5936b27fb3dacd2b1ad799e4de968ea697e7`；2026-10-20 查到權重問題仍 open，未取得明確澄清，本輪未下載／執行。不能以 repo Apache 或鏡像標籤代替該 checkpoint 的來源資格 |
| S04 | [MAT](https://github.com/fenglinglwb/MAT) 與 [LICENSE](https://github.com/fenglinglwb/MAT/blob/main/LICENSE) | 協調者複核非商業條款文字；原始碼脈絡由本機 lens 調查。不是 checkpoint／產品用途法律核准 |
| S05 | [Moebius README](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/README.md)；[模型設定](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/config/model_cfg/moebius.yaml)；[lambda attention](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/model_lib/nets/layers/%CE%BB/vanilla%CE%BB.py) | pin `b88d462bacb9af6e7128a3b4cc4a07418bedfd61`；第 9 節的單圖匯出未完成，第 10 節已用多 session 執行 3 組完整 native 模型鏈，但皆有 strict fail。小核心／大型模型等級仍是作者品質宣稱，不由本輪樣本證明 |
| S06 | [Moebius 固定模型卡](https://huggingface.co/hustvl/Moebius/blob/cd01f47fb648219d3fa605806c5ce00b713faa5e/README.md)；[PixelHacker 固定模型卡](https://huggingface.co/hustvl/PixelHacker/blob/012fd343158936a265b8a0ee38a791a7a2841f45/README.md) | 本輪取得上述版本 student／VAE；兩張 HF 卡均標 MIT，GitHub repo 為 Apache-2.0，Moebius README 明列 weights。差異照錄，未找到 VAE 獨立條款，不作產品散布的法律核准 |
| S07 | [ObjectClear repo](https://github.com/zjx0101/ObjectClear)；[實讀 LICENSE](https://github.com/zjx0101/ObjectClear/blob/c052d91ecd8772744a5ab97527441c813ab83009/LICENSE)；[原論文頁](https://arxiv.org/abs/2505.22636) | 協調者直接讀授權；固定 LICENSE `c052d91ecd8772744a5ab97527441c813ab83009`。論文版本／repo 副標不同不代表本機測過；不引用未複核的資料集數量 |
| S08 | [FLUX 官方 Fill 說明](https://github.com/black-forest-labs/flux/blob/802fb4713906133fcbd0d8dc5351620ca4773036/docs/fill.md)；[固定 repo 模型條款](https://github.com/black-forest-labs/flux/blob/802fb4713906133fcbd0d8dc5351620ca4773036/model_licenses/LICENSE-FLUX1-dev)；[BFL 現行公開條款](https://bfl.ai/legal/non-commercial-license-terms) | 協調者讀取；程式碼 pin `802fb4713906133fcbd0d8dc5351620ca4773036`。現行網頁條款不能無聲取代所選權重附帶版本，採用時重新審查適用版本與使用方式 |
| S09 | [SAM 2 README／License](https://github.com/facebookresearch/sam2/blob/2b90b9f5ceec907a1c18123530e92e794ad901a4/README.md) | 協調者直接複核 checkpoint／code Apache-2.0 文字；pin `2b90b9f5ceec907a1c18123530e92e794ad901a4`。原生 encoder／decoder 仍未部署 |
| S10 | [EfficientSAM exporter](https://github.com/yformer/EfficientSAM/blob/d525f622e6f640acf5a0fc37c7ca1f243da5bde0/export_to_onnx.py)；[LICENSE](https://github.com/yformer/EfficientSAM/blob/d525f622e6f640acf5a0fc37c7ca1f243da5bde0/LICENSE) | pin `d525f622e6f640acf5a0fc37c7ca1f243da5bde0`；已取得 Ti checkpoint、匯出 FP32 split 並原生串接，見第 9 節。來源為 Apache-2.0 repo 隨附權重，未找到獨立權重條款；不因此宣稱所有裝置可用 |
| S11 | [MobileSAM exporter](https://github.com/ChaoningZhang/MobileSAM/blob/f706ad9c4eb7f219c00d9050e46328518ffb65d2/scripts/export_onnx_model.py) | 協調者直接讀 decoder script；pin `f706ad9c4eb7f219c00d9050e46328518ffb65d2`。不能拿 `image_embeddings` 輸入當完整影像 encoder |
| S12 | [SAM 3 LICENSE](https://github.com/facebookresearch/sam3/blob/7f473cfd983d5eb43040ff608f66bff0d7bdcb53/LICENSE) | 協調者讀 current 與固定版條款，自訂 SAM License（文字日期 2025-11-19）；不繼承 SAM 2 的 Apache 判斷 |
| S13 | [Lightroom Classic Remove](https://helpx.adobe.com/lightroom-classic/desktop/process-and-develop-photos/remove-tool.html) | 協調者直接讀；頁面顯示日期與 metadata 更新日期不同，依本輪文字而非宣稱單一發版日。產品操作說明，不是 SDK、模型或離線執行證據 |
| S14 | [Lightroom mobile Generative Remove](https://helpx.adobe.com/lightroom/mobile/heal-clone-and-remove/remove-objects-with-generative-remove.html) | 協調者直接讀；顯示更新 2026-06-05。付費／網路要求是該產品的條件，不能外推為 FilmBatch 整合權利 |
| S15 | [Photoshop Remove](https://helpx.adobe.com/photoshop/using/tool-techniques/remove-tool.html) | UX lens 的原廠文件摘要，協調者未另行直接複核；僅補充互動線索 |
| S16 | [Apple Photos Clean Up](https://support.apple.com/guide/photos/remove-distractions-and-imperfections-pht5c38b77c5/mac) | UX lens 的原廠文件摘要，協調者未另行直接複核；不判定 Apple 模型內部、可嵌入介面或未測支援能力 |
| S17 | [Vertex AI 物件移除文件](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/image/edit-remove-objects) | 協調者直接讀公開文件；本輪未呼叫、未查帳務或區域可用性，文件樣例也須在實際採用時重驗，不能直接複製為部署配方 |
| S18 | [Stability AI SDXL VAE 模型卡](https://huggingface.co/stabilityai/sdxl-vae/blob/6f5909a7e596173e25d4e97b07fd19cdf9611c76/README.md)；[固定 artifact 樹](https://huggingface.co/api/models/stabilityai/sdxl-vae/tree/6f5909a7e596173e25d4e97b07fd19cdf9611c76) | pin `6f5909a7e596173e25d4e97b07fd19cdf9611c76`；第一方卡標 MIT。本輪只取得一份 safetensors、驗證 LFS SHA-256 並做 tensor／CPU 元件比較，結果見第 11 節；不是現有 PixelHacker VAE 的來源證明或產品法律核准 |
| S19 | [Moebius 場景微調 artifact](https://huggingface.co/api/models/hustvl/Moebius/tree/cd01f47fb648219d3fa605806c5ce00b713faa5e/ft_places2)；[論文 §4.1](https://arxiv.org/html/2606.19195v1#S4.SS1) 與 [OOD 表 7](https://arxiv.org/html/2606.19195v1#Sx1.T7) | 模型 repo 沿用 S06 pin；作者區分蒸餾與分領域微調。第 11 節先讀 metadata；第 12 節已取得精確 `ft_places2` 檔、嚴格載入並做同輸入參考比較，不把論文表格當本機畫質證據 |
| S20 | [PowerPaint 程式碼 MIT](https://github.com/open-mmlab/PowerPaint/blob/5b4c3d52291709fcec2a1870d987da693fd3549c/LICENSE)；[v2.1 卡](https://huggingface.co/JunhaoZhuang/PowerPaint-v2-1/blob/5ae2be3ac38b162df209b7ad5de036d339081e33/README.md) 與 [元件樹](https://huggingface.co/api/models/JunhaoZhuang/PowerPaint-v2-1/tree/5ae2be3ac38b162df209b7ad5de036d339081e33?recursive=true) | 程式碼 pin `5b4c3d52291709fcec2a1870d987da693fd3549c`；模型 pin `5ae2be3ac38b162df209b7ad5de036d339081e33`。卡標 Apache-2.0，但 base／VAE／文字元件權利另審；沒有下載、原生轉換或資源量測 |
| S21 | [BrushNet README](https://github.com/TencentARC/BrushNet/blob/0f9d9e54ca85c40a11a8f0504b4b5b2e7e8fd14d/README.md) 與 [Apache-2.0 條款](https://github.com/TencentARC/BrushNet/blob/0f9d9e54ca85c40a11a8f0504b4b5b2e7e8fd14d/LICENSE)；[具名 adapter 樹](https://huggingface.co/api/spaces/TencentARC/BrushNet/tree/0ae4b1d340aa8c60de67b117fcde71e4a2b06dbd/data/ckpt/segmentation_mask_brushnet_ckpt) | code pin `0f9d9e54ca85c40a11a8f0504b4b5b2e7e8fd14d`；Space pin `0ae4b1d340aa8c60de67b117fcde71e4a2b06dbd`。另需 SD base 等元件；本輪只讀來源及 metadata，未執行 |
| S22 | [Qualcomm AOT-GAN 卡](https://huggingface.co/qualcomm/AOT-GAN/blob/da4d45719d75f84e67c4def8623641a481bc6779/README.md)；[LICENSE](https://huggingface.co/qualcomm/AOT-GAN/blob/da4d45719d75f84e67c4def8623641a481bc6779/LICENSE) 與 [release 指標](https://huggingface.co/qualcomm/AOT-GAN/blob/da4d45719d75f84e67c4def8623641a481bc6779/release_assets.json) | pin `da4d45719d75f84e67c4def8623641a481bc6779`，release `0.64.0`；卡標 MIT，但 LICENSE 僅轉指另一個 AttnGAN repo，未建立原 AOT checkpoint 的明確來源鏈。卡列 CelebAHQ／512／58 MB，不外推一般攝影品質或 ORT 1.25.0 相容 |
| S23 | [SDXL-VAE-FP16-Fix 模型卡](https://huggingface.co/madebyollin/sdxl-vae-fp16-fix/blob/207b116dae70ace3637169f1ddd2434b91b3a8cd/README.md)；[固定 artifact 樹](https://huggingface.co/api/models/madebyollin/sdxl-vae-fp16-fix/tree/207b116dae70ace3637169f1ddd2434b91b3a8cd) | pin `207b116dae70ace3637169f1ddd2434b91b3a8cd`；卡標 MIT，作者描述基於 SDXL-VAE 0.9 微調並縮小權重／bias，保留些微輸出差異。第 12 節實測轉 FP16 後的參數內容一致，不代表歷史來源、推論等價或全鏈散布核准 |

**來源調查的停止點**：已取得區分方法、候選、選取出口、授權風險與 FilmBatch 插入點所需的原始來源；之後經使用者另行授權執行第 9 節實驗。其餘問題需要指定 artifact、品質資料或 Human Review，不能靠增加論文引用解決。

**仍未判定**：哪個 artifact 畫質／總操作成本勝出、最終採用版本、超出本輪固定尺寸／樣本的數值與裝置上限，以及手機完整生成功能。正式產品實作前仍須完成剩餘 T1 資格；新 checkpoint／exporter／license／ORT 會使相應資格過期。前置實驗不等於 T1／T5 或功能交付通過。

## 9. 本機可行性實驗

### 範圍、資料與工具

使用者授權先保存研究文件，再做隔離可行性實驗、依結果回寫。文件基線已於本機 commit `a8744a8`；第 9 節前置階段未 push、未修改產品程式或新增永久測試。這不是第 6 節的品質驗收；後續文件發布授權見第 11 節。

- 主機：Apple M3、16 GiB RAM；.NET SDK `10.0.402`、原生 ONNX Runtime `1.25.0`。CPU 為 sequential、intra-op 4／inter-op 1；CoreML 使用 `MLProgram`、`MLComputeUnits=ALL`、固定輸入及停用低精度 GPU accumulation。CoreML 內部使用 CPU／GPU／ANE 的比例未量測。
- 暫用獨立 Python `3.11.17` venv、PyTorch `2.8.0`、ONNX `1.18.0` 作匯出／參考，並非產品新增 runtime；精確版本留於 `environment-freeze.txt`。未變更全域 Python、SDK 或 Xcode。
- 共同輸入為非對稱 512×512 程序 RGB16 圖、13,592 像素 hole，以及 [NIND 公開照片](https://commons.wikimedia.org/wiki/File:NIND_droid_ISO200.jpg) 的 512×512 衍生裁片；作者 Trougnouf（Benoit Brummer），CC-BY-4.0。照片輸入另加合成紅色目標，不是自然物件／陰影移除 benchmark，也不是 sensor RAW。原照片 SHA-256 與 11 個 fixture 檔案檢查通過。
- 原生量測在匯出／參考工作結束後串行執行。主 wave 為 **20 組，18 組 strict-reference pass、2 組 fail**；另有 **12 組記憶體 smoke，10 pass、同一 VAE encoder 的 2 組 fail**。所有失敗均保留。

### 實際 artifact 與介面

下列是本次本機匯出的 **FP32、opset 17、固定輸入尺寸**圖；不是產品 catalog 或平台支援清單。來源 checkpoint 與匯出圖的 digest 分開保存於各 `cases.json`／來源帳本。

| ONNX artifact | bytes | SHA-256 |
|---|---:|---|
| MI-GAN-512 core | 28026345 | `37c1efb93fc5e2bb9f56b3d843298701d38542ddcd1069eaf4e3334512870e1c` |
| EfficientSAM-Ti encoder | 24799767 | `e9e444487896b028143e0138c792bebe55de36a5eccb1f2f1504efef3ee55db1` |
| EfficientSAM-Ti decoder | 16563593 | `64cf44d93606eca04912003c6428c99cb5535aa17147433106a1cd2a3b45e45b` |
| Moebius removal-step | 905108134 | `0731ddf7fecc2ad480ad457187a69ff4cd5acf27655a5a943a5829d9fe5b0e4f` |
| PixelHacker VAE encode | 136760895 | `68f3c5b6c5c69dece9519c7a9cff410fcd9f0fb2ef46f4b14854e5ae20b81d03` |
| PixelHacker VAE decode | 198069306 | `13666ed6ce3a4176298afe9181e660c15741856077d124b2efde62662c4cf89c` |

- **MI-GAN**：原 checkpoint 29,553,256 bytes，SHA-256 `1d6087eee0aac8923ad2606be5d8caeb4824d3e4de331995e420c74e124a466a`；依 S02 原 `Generator` strict 載入。輸入 float `[1,4,512,512]`，依序為 `known-0.5` 與 `(RGB*2-1)*known`，`known=1-hole`；輸出 `[1,3,512,512]`，以 `clip(out*0.5+0.5,0,1)` 映回 SDR。實測輸出可略超出名義 `[-1,1]`，不假設網路已自行 clamp。另取得的作者 `migan.onnx` 為 UINT8 pipeline，未拿它走 RGB16 整圖往返。
- **EfficientSAM**：原 Ti checkpoint SHA-256 `dff858b19600a46461cbb7de98f796b23a7a888d9f5e34c0b033f7d6eb9e4e6a`。encoder 接 RGB float `[1,3,512,512]`，上游 wrapper 內部 bilinear 放大到 1024，再輸出 `[1,256,64,64]` embedding；decoder 收同一 provider 的**原生 encoder 輸出**、像素座標、labels 與 int64 `[H,W]`。參考是上游 ONNX bilinear wrapper，不宣稱與另一條 bicubic eager 後處理相同。以預測 score 排序三個候選，再用 logits `>=0`；score 曾超過 1，不能視為校準過的信心機率。
- **Moebius**：student checkpoint 905,297,927 bytes，SHA-256 `6c9ee08ce467af48767b74416646d82f8a0c1dcfb558c478e635cef0ca8b2ad2`；VAE checkpoint 167,394,306 bytes，SHA-256 `a59d7ea697f2942d22002dc3469e8c53db807a6b78f7f5ec03bd4c1f70f98efe`。per-step 圖接 `[2,9,64,64]` CFG-concat、timestep 與 embedding IDs，本例 `t=950`；VAE latent 為 `[1,4,64,64]`。僅裁掉 package 的未用 teacher import，所有執行算子仍來自固定上游，沒有替代 attention／假類別。student checkpoint 另以顯式 `weights_only=True` 成功載入 1,203 個 tensor 項目；重現時須固定 restricted-load 政策，不因副檔名或 tensor key 就假定 pickle 安全。

### 原生執行、延遲與程序記憶體

每組先載入 session、首跑，再量 **10 次 warm run**，p50／p95 採 nearest-rank；開啟 ORT profiling。這些只含該 graph 推論，不含 decode、完整現像、ROI／合成、UI；cold load／first run 分列在原始結果，OS cache 未清空。表列合成圖與正點 decoder；照片及其餘 prompt 的完整 20 組資料保留於 `summary.json`。

`.NET PeakWorkingSet64` 在本輪回報 0，**作 unavailable，不當成零記憶體**；表中 peak RSS 來自另外 12 組 `/usr/bin/time -l` smoke（首跑＋1 次 warm、無取消），不是主 wave 的同步峰值。它不含其他 CoreML 服務程序，也不是完整 app、多元件同時存活或 iOS 峰值。

| graph | CPU p50／p95 ms | CoreML-request p50／p95 ms | 獨立 peak RSS CPU／CoreML MiB | CoreML-request 的 CoreML／CPU event names | 參考數值 |
|---|---:|---:|---:|---:|---|
| MI-GAN-512 | 271.34／329.31 | 74.12／105.52 | 987.7／1274.6 | 8／14 | pass |
| EfficientSAM encoder | 1265.73／1344.62 | 1316.24／1358.32 | 1006.6／1026.4 | 18／353 | pass |
| EfficientSAM decoder，正點 | 26.96／30.02 | 32.70／39.32 | 228.2／238.1 | 14／415 | pass |
| Moebius removal-step，單步 | 7149.73／24133.69 | 3813.24／4668.73 | 3346.2／5207.9 | 47／138 | pass |
| VAE encode | 8389.64／11059.11 | 1190.09／1405.94 | 1296.6／600.6 | 1／1 | **fail** |
| VAE decode | 17686.34／18945.04 | 2340.91／2982.80 | 2324.3／776.4 | 1／0 | pass |

event names 為 profile 中唯一已執行 kernel 名稱數，不是原始節點百分比，也不能證明 CoreML partition 內全部跑 GPU／ANE。MI-GAN 與 SAM 均是混合 provider；這個樣本下 SAM 的 CoreML-request 並未比 CPU 快，不預設「有 CoreML 就該優先」。單步 Moebius 成功不等於完整生成時間可由表格直接相加或乘步數得出。

### 已驗證的窄邊界與失敗

- **數值**：以同權重 PyTorch 參考檢查有限值，統一 strict gate 為 `abs(error) <= 0.0002 + 0.002*abs(reference)`。20 組皆有限值；18 組通過。VAE encoder 圖含 `RandomNormalLike`，跨 runtime 噪聲未對齊；CPU／CoreML 分別有 35／30 個 latent 元素超界，max-abs 為 0.004888／0.007498。保留 fail，不加寬容差或把 sample 換成 mean；也未證明全部誤差只來自 RNG。
- **選取**：正點、正負點、框、框＋正點在兩種 provider 共 8 組，均串接原生 embedding。對同一程序圖紅色目標的實際 mask IoU 為 0.9631–0.9921；只證明這張已知幾何的基本行為，不是攝影品質通過。非最佳候選可能覆蓋背景，不能固定拿 candidate 0。
- **RGB16／crop-back**：MI-GAN 的程序圖／照片、兩種 provider 共 4 組，二元 mask 外 RGB 變動皆 0，非零原點 `(137,83)` 放回大畫框後外部變動也為 0。反轉 mask 的負控制確實偵測到越界；4 組 PNG16 經獨立 OpenCV decode 逐值相等，其中 1 組再經 Apple ImageIO 解碼確認 16-bit sRGB 全部 RGB samples 相等。這不是完整效果管線、羽化、alpha、專案或接受結果重播驗收。
- **取消／重用**：MI-GAN 兩種 provider 均在 native 尚未完成時要求 `RunOptions.Terminate`，觀察到 terminate exception；取消至 native 返回分別約 1.80／2.71 ms，同 session 隨後成功再推論並通過參考檢查。probe 有 post-run token 丟棄；未測產品 UI 回到可互動、世代／快取或 undo 行為。
- **負控制**：缺少模型、模型 SHA-256 不符均明確失敗，沒有回原圖假成功；這是 probe 的邊界，不是尚不存在的產品 API 測試。
- **Moebius 完整鏈**：真實 PyTorch CPU 參考以 seed 0、要求 20 DDIM steps、CFG 2.5、paste 執行，已檢視紅目標被填補；上游此介面仍是 PIL RGB8 輸入／輸出，不是合格 RGB16 原生鏈。2-step 單圖匯出兩次得到 SIGKILL／137，無獨立 memory-kill 證據，OOM 僅為推測；1-step 到寫檔時得到 `OSError: [Errno 28] No space left on device`，同時保留 15 GiB 可用的 df snapshot，不臆測原因或保證重試成功。0-byte 未完成圖已移除，完整錯誤仍在；沒有完整 native sampling／CFG／DDIM／VAE／paste 串接通過證據。
- **權利與平台**：LaMa 權重資格未解，未下載／跑分；Moebius／PixelHacker 的 repo 與 HF 標籤差異尚待採用審查。未測 40 場景／3 位人類、Windows DirectML、iOS Simulator／實機移除，未做 FilmBatch UI／專案／輸出整合。不得把它們計成通過。

### 證據、重現與下一個 gate

本機忽略目錄 `dist/object-removal-feasibility-20261020/` 保存 `ledger.json`、`summary.json`、`native/runs.json`、`native-memory/summary.json`、`inspection/result.json`、三份候選 `cases.json`、來源／授權與 profiler／原始錯誤。已檢視圖包括 `inspection/native-fills-contact.png` 與 `moebius/reference/ref-steps20-cfg2.5-paste.png`；協調者的目視觀察不是人類盲評，格線仍可見局部不規則，不以漂亮小圖宣告畫質勝出。

主量測原始 profile 集中保留於 `profiles-original/`，以 `profile-relocation.json` 對照各報告的原始名稱；原波次原始碼另存 `verification-harnesses-primary-wave.tar.gz`。最後封存只修正 profiler prefix／enable 的設定順序並另行 smoke，不改量測、模型或數值判準。

驗證用原始碼封存為 `verification-harnesses.tar.gz`，沒有新增產品 runtime 或把腳本當正式功能。重現這次本機 smoke 可先將封存解回同一目錄，再使用保留的 venv／模型與 manifest 執行：

```bash
tar -xzf dist/object-removal-feasibility-20261020/verification-harnesses.tar.gz -C dist/object-removal-feasibility-20261020
"$HOME/.dotnet/dotnet" build dist/object-removal-feasibility-20261020/native-runner/NativeRunner.csproj -c Release --disable-build-servers -p:UseSharedCompilation=false --nologo
dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-feasibility-20261020/run_native.py
dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-feasibility-20261020/measure_memory.py
dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-feasibility-20261020/inspect_outputs.py
dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-feasibility-20261020/summarize_results.py
```

上述資料不隨 repo 發佈；manifest 含這次工作站絕對路徑，搬移時需重新產生路徑／參考，不能當成跨機安裝器。原生 smoke 不下載模型；若重跑 PyTorch 參考／匯出，先固定來源、環境與 `TORCH_FORCE_WEIGHTS_ONLY_LOAD=1`。

**前置波次決定**：MI-GAN FP32 core 與 EfficientSAM split 有繼續研究的本機證據，但不代表已採用或 T1 通過。續驗已執行固定噪聲與完整原生模型鏈，並發現真實照片的反例；目前採用決策以下節為準，以上原始失敗不追改為通過。

## 10. 續驗與採用決策

### 決策與適用範圍

使用者另行要求繼續實驗並決策。第 10 節波次只延續忽略目錄內的研究與既有文件回寫，當時沒有產品實作、新 commit 或 push；原文件基線 `a8744a8` 與第 9 節證據保持不變。第 11 節再補來源實驗與文件發布範圍。

| 項目 | 本輪決定 | 重議條件 |
|---|---|---|
| 通用物件移除／T1 | **NO-GO；沒有合格產品預設，不開始 T2–T5** | 合法 artifact、數值／資源與固定品質 gate 均通過；不降低原先要求 |
| MI-GAN FP32 core | **保留工程對照，不採用為通用移除預設** | 獨立場景證明目標／影響真正消失，無顯眼新物件且通過既有盲評；目前沒有可宣稱已支援的小洞範圍 |
| EfficientSAM-Ti | **可繼續選取提案研究，不自動套用**；手動筆刷／修正不可移除 | 候選需人工確認；正負點、鄰近受保護物、影子／倒影與候選排序須另外驗收 |
| Moebius＋現有 VAE | **暫緩**，不再以單圖匯出重試為主線 | 定位剩餘數值漂移、精確後處理、明顯品質收益、可接受的整鏈成本與完整權利來源 |
| LaMa／受限候補／雲端 | LaMa 保持資格 blocked；其他不新增下載或呼叫 | 第一方權重資格或明確用途裁決；雲端仍需另一份隱私／成本授權 |
| 平台 | 僅有 **macOS M3、16 GiB、ORT 1.25.0 的研究證據** | Windows DirectML、iOS 及完整產品各自實跑；不以本機結果宣稱支援 |

保留現有 `BasicDisplay`／`ToneSplit` 後、`Detail` 前的修補平面、接受結果保存與離線重播設計。不新增 Python 後備、通用 provider plugin、另一個填補引擎或假成功路徑。這不是取消原需求，也不是把小型瑕疵工具偷換成通用移除功能。

### 真實照片、選取與合成續驗

- 輸入在推論前固定：同一 NIND 場景的 ISO 200／6400 **兩個檔案，不是兩個獨立場景**；各 6 個遮罩，共 12 組。涵蓋原尺寸紋理小洞、50.82% 大洞、貼邊、細線，以及 1344×1344 裁片縮為 512 的真實玩偶／接觸陰影。沿用第 9 節 CC-BY-4.0 來源與 SHA-256；沒有私人照片外送。
- MI-GAN 的 12 遮罩×CPU／CoreML-request 共 **24 組**，EfficientSAM 的 2 encoder＋8 prompt decoder 共 **10 組**；**34／34 strict pass**。decoder 使用實際 native encoder 輸出。另以 NumPy 從原始 tensor 獨立重算 **50 個輸出比較**全部通過，沒有只相信 C# 的成功標籤。
- 這些是數值通過，**不是品質通過**。實際檢視兩種 ISO 的大洞皆出現新的紅／彩色形狀，玩偶移除區域出現不相關棕色形狀；MI-GAN 不宜作通用移除預設。這是可見反例，不是人類盲評或家族整體排名。
- SAM 的單正點偏向只選身體；最高預測分的正負點候選反而涵蓋兩側玩偶。框／框＋點較接近目標，仍需人工修正。分數不是安全保證，負點不是絕不越界的約束。人工 polygon 為近似標註，其 IoU 只作診斷，不作真實選取準確率。
- **48 組** RGB16 合成：12 遮罩×2 provider×硬邊／向內 8px 羽化。二元遮罩外 RGB、非零原點 crop-back 外部變動均為 0；48 個 PNG16 往返逐值相同。alpha 為借用的合成測試平面，並非照片原生透明度或產品 alpha 語意資格。1 組再由 Apple ImageIO 獨立解碼，1,048,576 個 RGBA16 samples 全數相同。原始照片雜湊未變。

每組 5 次 warm graph 推論，以下將同組樣本合併取 nearest-rank p50／p95；CPU 為 sequential、intra-op 4。開啟 profiling，不含影像 decode、完整 ROI／現像／UI；peak RSS 是 `/usr/bin/time -l` 的 probe 程序峰值，不含其他 CoreML 服務。與第 9 節不同輸入／波次，不能解讀成產品最佳化前後差異。

| graph／provider request | cases／warm samples | pooled p50／p95 ms | 最大程序 RSS MiB |
|---|---:|---:|---:|
| MI-GAN CPU | 12／60 | 198.84／207.45 | 998.5 |
| MI-GAN CoreML-request | 12／60 | 60.85／75.86 | 1275.9 |
| SAM encoder CPU | 2／10 | 905.26／920.09 | 1021.5 |
| SAM decoder CPU | 8／40 | 18.12／18.52 | 243.6 |

MI-GAN CoreML-request profile 仍含 8 個 CoreML／14 個 CPU kernel event names，不是全 GPU／ANE。SAM 本輪只補 CPU；先前 CoreML-request 未見收益的證據仍在，不假定所有模型共用同一最佳 provider。

### Moebius：完整模型鏈可執行，但未合格

不再展開巨大單圖。重用既有 denoiser／decoder，加上顯式 epsilon 的 VAE encoder，C# host 自 image／mask／噪聲開始計算兩次 VAE sample、初始加噪、CFG＋DDIM、decode 與貼回；**沒有把參考 latent 餵入冒充整鏈成功**。同固定 seed 的參考由真實上游模組／scheduler 生成，記錄每步供比較。

- `num_steps=20`、`strength=0.99` 的實際上游行為為 **19 步，t=900…0**；不是先前單步範例的 t=950。eps 已外部化，沒有用 posterior mean 取代 sample。
- 新 encoder 使用相同權重與數學式，但為降低原 exporter 的 normalization 累加誤差，另有具名 GroupNorm→分塊 ReduceMean lowering；不是未改動的作者 ONNX。實跑圖為 138,528,854 bytes，SHA-256 `4b4678abf23e9e9406cd97da34fbccc9f867d22b8de354bf9bad0bf45b9907b0`；原始 epsilon 圖與改寫來源／digest 均保留。
- 固定輸入重跑差異為 0、無 Random 算子，**但 VAE strict 比較仍有 19／16,384 個元素失敗，max-abs 0.005284**。fp64-cast 與 fp32 PyTorch 自身亦有差異，是敏感度診斷，不是正確性豁免或修改容差的理由。
- 合成圖兩個 provider 的 pre-paste 圖皆 strict pass，但中間階段與最終貼回仍 fail。真實玩偶照片 CPU pre-paste 有 **33／786,432** 個元素超界，max-abs **0.002469**；剩餘原因未全部定位，不能把所有失敗歸因於 RNG 或參考精度。
- 貼回使用近似 Pillow blur，並非 bit-exact 上游後處理，還有 1 LSB 差異；`1/255` 大於本例容差的最大值 `0.0022`，**不算通過**。host 的「外部不變」檢查只涵蓋 blur 後 matte=0；獨立重算發現原二元 mask=0 區域仍被 blur 影響，因此不能拿它當產品嚴格遮罩外保留證據。產品仍須以正式確認 matte 作高精度合成。

下列每格只有 **1 次完整鏈**，不是 p50／p95；chain 不含 session load、PNG／來源 decode，程序 wall 另包含載入／比較，CPU 合成圖亦含一次 denoiser 取消／重用 smoke。

| 完整模型鏈 | session load s | chain s | 程序 wall s | peak RSS GiB | strict 整體 |
|---|---:|---:|---:|---:|---|
| 合成圖 CPU | 0.84 | 98.64 | 105.04 | 6.36 | fail |
| 合成圖 CoreML-request | 28.36 | 46.86 | 76.13 | 7.46 | fail |
| 真實玩偶 CPU | 0.85 | 114.60 | 116.61 | 5.26 | fail |

CoreML-request 留下約 2.3 GiB cache；RSS 不包含外部服務程序。此結果未建立手機可行性。CPU denoiser 的 raw terminate／同 session 重用成功，但不等於完整鏈 UI 取消驗收。目視玩偶本體移除後仍見碎片／塗抹；人工 mask 近似，不能把所有殘留歸因於模型。這些結果不足以抵銷數值、權利與資源缺口。

### 權利裁決與重現

- MI-GAN 的第一方 [LICENSE-WEIGHTS](https://raw.githubusercontent.com/Picsart-AI-Research/MI-GAN/2b793c5ece43f4253e32d4afc257120a5deed6f5/LICENSE-WEIGHTS) 明列 MIT；EfficientSAM [README](https://raw.githubusercontent.com/yformer/EfficientSAM/d525f622e6f640acf5a0fc37c7ca1f243da5bde0/README.md) 將 checkpoint 放在 Apache-2.0 repo 中。這些足以支持繼續工程研究；日後散布須保留 notices，訓練資料政策與產品資格另審，不宣布法律清白。
- Moebius 第一方明列 student 權重 Apache-2.0 商用許可，但 HF MIT metadata 差異與 exact VAE 的來源仍需釐清。[PixelHacker 論文 §4.2](https://arxiv.org/html/2504.20438v1#S4.SS2) 說使用 frozen SDXL-VAE，不能單憑名稱／scaling factor 直接替換。第 10 節波次只有現測 `.bin` 與官方檔案 metadata，當時 tensor 等價未知；第 11 節已實際取得官方 safetensors，證明不是單純 FP16 轉換即可相等。原始失敗不追改，也不據此判定 rehost 違法。
- [LaMa issue #368](https://github.com/advimman/lama/issues/368) 本次讀取仍 open、comments=0；檢視的 README／LICENSE 未建立 exact checkpoint grant。這不是已證明全世界沒有授權，而是本專案資格 gate 未滿足；因此不下載、不宣稱被其他模型擊敗。

續驗證據集中於 `dist/object-removal-decision-20261020/`：`ledger.json`、`photos.json`、`native-photos/summary.json`、`numeric-verification.json`、`inspection/result.json`、`rights-review.json` 及 `moebius/result.json`。檢視圖為 `inspection/iso200-fills-contact.png`、`inspection/iso6400-fills-contact.png`、`inspection/selection-contact.png` 與 Moebius native PNG。這些不是人類盲評紀錄。

驗證原始碼封存於 `verification-harnesses.tar.gz`，沿用前一輪 venv／權重／絕對路徑；模型、參考 tensor、失敗與 runtime receipts 不因封存而刪除。T1 維持未通過；重新啟動產品實作的條件是既有資格與品質 gate 真正完成，不是再累積更多跑通的小圖。


## 11. 權重來源與後續研究

本節是前一輪來源研究的紀錄；其「未下載／未測 `ft_places2`」及下一步建議，已由[第 12 節](#12-場景微調vae-身分與原生續驗)的後續實驗更新。舊 VAE／原生數值失敗仍原樣保留。

### 本輪範圍與決定

使用者進一步授權「繼續研究，並提交文件並推送」。發布範圍僅限目前研究分支的 Survey、ADR、操作設計、實作計畫與文件索引；不合併 `master`，不納入模型、照片、參考 tensor 或實驗目錄。產品程式與 T1／T5 判準未變，T2–T5 仍未開始。下列來源查核與實驗均為 2026-10-20；新 checkpoint、設定、exporter 或授權版本需重新取得資格。

**決定仍是通用移除 NO-GO。** 本輪新增的是「現有 VAE 不可視為官方檔的簡單 FP16 版本」及「先前只測 Moebius `pretrained`，不能當成 `ft_places2` 的品質結果」。沒有藉此宣告新模型勝出、舊失敗通過或產品可用。

### 官方 VAE：相同架構不等於相同權重

S18 的官方 `diffusion_pytorch_model.safetensors` 為 **334,643,268 bytes**，SHA-256 `1598f3d24932bcfe6634e8b618ea1e30ab1d57f5aad13a6d2de446d2199f2341`，與固定 repo 的 LFS OID 相同。只下載這一份候選權重，沒有新 runtime。現有 PixelHacker `.bin` 沿用第 9 節 `a59d7ea6…`，顯式 `weights_only=True` 並設 `TORCH_FORCE_WEIGHTS_ONLY_LOAD=1`。

- key／shape **248／248 相同**，共 **83,653,863** 個元素；現有檔全 FP16、官方全 FP32。把現有值提升為 FP32 後，**0／248 tensor 完全相等**；把官方先轉 FP16，仍只有 **4／248** 相等，為 `quant_conv`／`post_quant_conv` 的 weight／bias。最大權重絕對差約 **55.429**。這不是只靠檔案大小猜測；另以 JS 解析官方 safetensors binary header，獨立確認 key、shape 與元素數。
- 比較的是數值，不是來源權利。部分權重的縮放關係僅留作 lineage 診斷；**尚未辨識精確轉換或證明作者來源**。官方模型卡的 MIT metadata 也不能替現有 `.bin` 完成來源／散布審查。
- 設定仍分開記錄：現有 `sample_size=512`、`force_upcast=false`，官方 `sample_size=1024`、未明列 `force_upcast`。實驗固定 512、CPU FP32 計算，兩份設定均可執行；不把相同 channel／scaling factor 當作等價或免驗更換的依據。

實跑同一 `synthetic512` 與真實 `iso200-subject-object` 的既存 image、記錄 epsilon 與 decode latent。參考明確為「現有 checkpoint 在相同 CPU FP32 路徑的輸出」；encoder 保留 posterior sampling，不換 mean；decoder 用共同 latent。沿用 `abs(candidate-reference) <= 0.0002 + 0.002*abs(reference)`，不是比較模型誰畫得較好，也不是重跑原 ORT 失敗。

| 共同輸入／元件 | 官方原始 FP32 相對現有檔的超界元素 | 最大絕對差 |
|---|---:|---:|
| 合成圖 encode，含相同 epsilon／scaling | 14,413／16,384 | 2.310204 |
| 合成圖 decode，映至 `[0,1]` | 492,212／786,432 | 0.051611 |
| 真實照片 encode，含相同 epsilon／scaling | 13,775／16,384 | 1.643790 |
| 真實照片 decode，映至 `[0,1]` | 588,644／786,432 | 0.106013 |

官方「原始 FP32」與「先 FP16 再 FP32」各作上述 4 組比較，**8／8 皆有限值但等價性 fail**。因此不直接換用官方 VAE、不沿用舊 denoiser／參考輸出身分；這不代表官方 VAE 本身品質差，也不證明 PixelHacker 權重來源違法。未匯出官方 VAE 為 ORT 圖、未跑替換後完整移除鏈、未做其畫質或平台資格。

### Moebius：預訓練與場景微調需分開判斷

S06 的固定 HF repo 同時列 `pretrained`、`ft_places2`、`ft_celebahq`、`ft_ffhq`。S19 論文 §4.1 描述蒸餾後再做分領域微調，OOD 表 7 使用 Places2／CelebA-HQ 權重；這是作者評估設定，不是 FilmBatch 已重現該表。

| student artifact | bytes | SHA-256／狀態 |
|---|---:|---|
| `pretrained/diffusion_pytorch_model.bin` | 905,297,927 | `6c9ee08ce467af48767b74416646d82f8a0c1dcfb558c478e635cef0ca8b2ad2`；第 9／10 節已測 |
| `ft_places2/diffusion_pytorch_model.bin` | 905,298,356 | `6525afb888e55f9b5c74fa0a5d19ca0762d720d6c716fb0f8422fbeb6868a09a`；第 11 節當時只核對 metadata，後續下載／實測見第 12 節 |

不能由兩檔大小接近或共用模型設定就宣稱 tensor keys／shape 或 runtime 100% 相容。現有反例適用已測 `pretrained`，不能移植為 `ft_places2` 的品質判決；反過來，微調名稱也不保證消除碎片、新物件或資源問題。原生 strict fail 與高成本仍是真實、未撤銷的證據。

固定上游 pipeline 的 CFG 輸入由 learned embedding IDs 組成：conditional `0…9`、unconditional `10…19`，按 `[uncond,cond]` 組合；不是缺少一段文字 prompt 或未安裝文字 encoder 才失敗。`ft_places2` 仍需重新取得本身的 tensor／數值／品質資格，並由 owner 審查 student grant、資料 lineage 與實際使用的 VAE；不由本文件裁定資料條款是否傳遞。

### 限量候補查核，不新增完整模型鏈

| 路線 | 本次查到的具名材料 | 缺口與處置 |
|---|---|---|
| PowerPaint v2.1 | S20；`PowerPaint_Brushnet/diffusion_pytorch_model.safetensors` 為 3,544,366,408 bytes，OID `530f2886ef5bcdf199269ec344155a517639ba64219b85eeb23fd86aab93147f`；另列 Realistic Vision UNet、VAE、文字與安全元件 | code MIT 與模型卡 Apache-2.0 不覆蓋所有 base 元件；未下載、未轉換、未量測本機 RAM／延遲。不以單一 adapter 大小估全鏈或手機上限 |
| BrushNet | S21；`segmentation_mask_brushnet_ckpt/diffusion_pytorch_model.safetensors` 為 2,475,354,520 bytes，OID `066793867083310c25b0d57d300b1ecdc02cb708a155060f020be64afb2ae9c3`；另需 SD base／VAE 等元件 | repo／Space 的 Apache-2.0 與第三方 base 條款分開；不是可獨立移除的單一 adapter。原生閉包、記憶體、速度及攝影品質皆未測 |
| AOT-GAN | S22 的 Qualcomm 發布卡列 CelebAHQ checkpoint，release 指向 ONNX float ZIP；MIT metadata 與 LICENSE 的來源指標不一致 | 原始權重資格未閉合，不下載；不能拿臉部模型或 Qualcomm 平台的作者速度當一般攝影／macOS／iOS 證據。也不由 MI-GAN 的反例判定所有非 diffusion 模型失敗 |

SD／Realistic Vision 類基底還需核對實際 CreativeML OpenRAIL-M 等條款；不能把 OpenRAIL 一律記成非商業，也不能把最外層 MIT／Apache 標籤當作全鏈商用許可。上述僅是可追溯的候補篩查，不是完整法律意見或畫質排名。

**下一個建議實驗只有一條：有條件的 `ft_places2` 對 `pretrained` 開發集比較。** 先固定具名權重、VAE 來源、用途／資料政策與資源上限，再於相同 mask／seed／前後處理執行作者參考比較；先用既有失敗反例與 T1 的 16 張開發集判斷是否有值得繼續修原生數值／精確貼回的品質收益。**24 張 T5 留出集不拿來調參或挑 checkpoint**。若反例仍在，只拒絕該 checkpoint／設定；若改善，也仍須通過原有數值、完整鏈、3 位人類品質與各平台 gate。最強反方是先前整鏈 47–115 秒與高 RSS 不會因換同架構權重自動消失；有可見且必要的品質收益，才值得繼續工程投入。本輪未下載微調權重、未啟動另一套模型鏈。

### 證據與重現邊界

新增忽略目錄 `dist/object-removal-research-20261020/` 保存 `ledger.json`、`vae/result.json`、`vae/per-tensor-report.json`、`vae/paired-inference.json`、執行 receipts、來源／config 雜湊與 `vae/parent-verification.json`。模型卡與 tensor 報告是不同證據；完整記錄保留原失敗，不把 process exit 0 解讀成數值合格。驗證腳本封存於 `verification-harnesses.tar.gz`，不隨文件 commit 發布。

重現 VAE 比較需保留原 venv、固定官方 safetensors、現有 `.bin` 與第 10 節參考輸入，解開封存至原忽略目錄後執行；腳本不自行下載模型，搬機需先更新 manifest／來源路徑：

```bash
TORCH_FORCE_WEIGHTS_ONLY_LOAD=1 dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-research-20261020/vae/compare_tensors.py
TORCH_FORCE_WEIGHTS_ONLY_LOAD=1 dist/object-removal-feasibility-20261020/venv/bin/python dist/object-removal-research-20261020/vae/paired_inference.py
```

原第 9／10 節的 artifact、失敗、參考與 profiler 保持不變。本輪只縮小來源與選型未知項，沒有宣告 T1 通過、產品整合完成或 Windows／iOS 支援。

## 12. 場景微調、VAE 身分與原生續驗

### 選擇與實驗邊界

使用者接續要求「從最可行的方案開始實驗並考慮授權及可移植性」。選擇 S19 的 **Moebius `ft_places2` 對 `pretrained`**：已有真正的 student、VAE、參考與分 session 原生鏈，新增具名場景權重即可比較，不需先引入 PowerPaint／BrushNet 的另一套大型基底。這是最小新增相依面的研究路線，不是先宣告畫質或速度勝出。本輪未改產品程式、未 commit／push。

固定第 10 節 NIND 同一場景的 ISO 200／6400 原檔、既有 RGB／mask 雜湊，使用 `iso200-subject-object`、`iso6400-subject-object`、`iso200-subject-contact-shadow`、`iso200-texture-large` 四案；最後一案遮罩約占 50.82%。作者參考新增四案 `ft_places2` 與三案 `pretrained`；舊 ISO 200 主體參考唯讀沿用，沒有重跑原失敗。條件仍為 512×512、seed 0、CFG 2.5、requested steps 20／strength 0.99、實際 19 步、noise offset 0.0357、posterior sampling 與原作者前後處理。四案是**單一場景的工程反例**，不是四個獨立場景，不取代 16 張開發集、24 張 T5 留出集或三位人類盲評。

### 精確 student 與 VAE 內容

| 本輪材料 | bytes | SHA-256 |
|---|---:|---|
| 場景 student `ft_places2/diffusion_pytorch_model.bin` | 905,298,356 | `6525afb888e55f9b5c74fa0a5d19ca0762d720d6c716fb0f8422fbeb6868a09a` |
| 新 FP32 ONNX denoiser，opset 17 | 905,108,134 | `4d6a39aff749e2c93a6b8221ce8b89831f5c7953ac75520a5b85724c79894b29` |
| S23 `diffusion_pytorch_model.safetensors`，僅作內容比較 | 334,643,238 | `1b909373b28f2137098b0fd9dbc6f97f8410854f31f84ddc9fa04b077b0ace2c` |

student 兩檔均以顯式 `weights_only=True`、`TORCH_FORCE_WEIGHTS_ONLY_LOAD=1` 載入實際模型，`strict=True` 沒有 missing／unexpected keys。共用的 1,203 個 tensor key 無 shape／dtype 差異，但**沒有任何 tensor 全等**，包括 learned embedding；不是把相近檔案大小當作相容證據。四對作者參考的影像、遮罩、噪聲與 VAE 初始條件一致；輸出差異不能歸因於偷換 seed／VAE。

S23 與第 11 節的 S18 官方原始 VAE 是**不同候選**，不能混稱「官方檔」。實測現有 PixelHacker `.bin` 的 FP16 payload，與 S23 FP32 權重**做一次 FP16 rounding 後逐位元一致**：

| VAE 內容檢查 | 結果 |
|---|---|
| key／shape 與元素數 | 248／248 相同；各 83,653,863 個元素 |
| S23 原始 FP32 與現有 FP16 提升值全等 | 0／248 tensor；有非零 rounding 距離 |
| S23 轉 FP16 與現有 payload 的 bitwise equality | 248／248 tensor；0 個不同位元模式；無非有限值 |
| 獨立複核 | JS 解析 safetensors header／串流 SHA-256；NumPy raw mmap／FP16 cast 後比較 `uint16`，不是重用 worker 的 `torch.equal` 判定 |

這縮小了參數內容關係，**不證明誰從誰複製、歷史來源或訓練 lineage**。設定檔的 provenance 與部分缺省欄位仍不同；沒有以相同 scaling factor 推定功能等價。本輪沒有將 S23 原始 FP32 換進移除鏈，也沒有執行它的 encode／decode；參考與原生實驗仍使用既存 PixelHacker VAE。第 11 節 S18 的八組等價性 fail 與先前 ORT VAE 漂移均未撤銷。

### 畫質反例與授權處置

協調者直接檢視四對實際輸出，而非只採信 worker 的判語：

| 固定案例 | 觀察與採用影響 |
|---|---|
| ISO 200 主體 | 兩版均大致移掉中央主體，但綠色孤立碎片與不自然延伸仍在；沒有看到值得推進產品的必要改善 |
| ISO 6400 主體 | 場景版同樣保留綠色碎片／延伸；不能由另一 ISO 當作另一獨立場景 |
| 主體加接觸陰影 | 擴大涵蓋範圍仍未解除碎片反例；不能將問題直接歸因為使用者少畫遮罩 |
| 大面積牆面紋理 | 原圖只有牆面紋理，兩版均生成顯眼的白色羽瓣狀形體，並改變紋理；命中預先固定的「產生新顯眼結構」反例 |

兩版像素確有差異，不是同一個結果檔；上述只是在固定案例／條件下未解除採用反例，不是宣布所有 Moebius 或 diffusion 方法都失敗，也不是人類品質排名。

權利仍逐鏈分開：S05 README 明列 Moebius code／pretrained weights 的 Apache-2.0 與允許商用文字，S06 HF 卡的 MIT metadata 差異照錄；S23 卡的 MIT 與 SDXL-VAE 0.9 基底描述亦保留。**研究取得資格不等於產品散布核准**；owner／法律審查仍須處理實際採用檔、PixelHacker 標籤差異、基底與訓練資料 lineage、必要通知。內容 bitwise 相符不補出權利讓與證據。LaMa 仍未取得具名權重澄清，沒有下載；未增加其他完整模型鏈。

### 新 artifact 的原生可移植性與成本

在同一部 Apple M3／16 GiB，以既有 .NET 10／ONNX Runtime `1.25.0` 執行新圖，沒有增加產品 runtime。新 denoiser 直接消費本身 PyTorch 參考的第一步與中段輸入；固定 `abs(native-reference) <= 0.0002 + 0.002*abs(reference)`：

| CPU 單步案例 | 最大絕對差 | 超界元素 | warm p50／p95，ms |
|---|---:|---:|---:|
| step 00，t=900 | 0.000003815 | 0／32,768 | 4,276.64／4,278.32 |
| step 08，t=500 | 0.000004351 | 0／32,768 | 4,315.43／4,487.81 |

兩案均 strict pass；各五次 warm，計時只含模型執行且開啟 profiling，不是 full-frame 或尾延遲保證。實際 profiler 為 CPU provider，不推定 CoreML／ANE。原生 `RunOptions.Terminate` 在兩案分別約 3.98／4.04 ms 返回，隨後同 session 可重用；只證明 raw native 取消／重用，**不證明產品 UI／cache 生命週期**。

完整鏈從 image／mask／noise 重新計算兩次 VAE encode、19 次 denoiser／CFG／DDIM、VAE decode 與貼回；參考 latent 只作比較，沒有作為原生中間輸入：

| 完整鏈 CPU 案例 | 鏈時間，s | 系統 max RSS，GiB | VAE encode 超界，原圖／遮罩圖 | pre-paste 超界 | 最終貼回超界 |
|---|---:|---:|---:|---:|---:|
| ISO 200 主體 | 99.74 | 6.797 | 0／1 | 39 | 998 |
| ISO 6400 主體 | 102.15 | 6.795 | 0／0 | 1 | 1,044 |
| 主體加接觸陰影 | 105.17 | 6.795 | 0／2 | 135 | 1,627 |
| 大面積紋理 | 113.00 | 6.801 | 2／8 | 14 | 2,292 |

**四案都完整執行，但四案 strict fail，不能將「跑得動」寫成可採用。** VAE／逐步數值漂移與近似 Pillow 貼回仍在；ISO 200 最終最大差約 2／255，不能統稱只剩一個 LSB。每案僅一次完整鏈計時，含嚴格比對、checkpoint I/O 與 profiling，不含影像 decode／PNG／產品 UI；RSS 取 `/usr/bin/time -l` 的完整 probe process，不使用此 Mac 上為零的 .NET peak 欄位，不是手機上限。

NumPy 從實際原生 `.f32` 逐項重算 186 組比較，與 C# 的超界計數全部一致，其中 77 組比較 fail；**獨立核對成功不表示數值通過**。四對參考輸入逐位元一致，原照片及凍結輸入雜湊未變。每個 encode／denoiser／decode session 使用獨立 profiler 路徑並核對實際事件；不是圖節點百分比。模糊 matte 權重為零處保留輸入 bytes；這不是原二值遮罩之外嚴格零變動的宣稱。

新 artifact 的 **CoreML 未跑**：CPU wave 後剩約 6.23 GiB，無法同時容納前次觀察約 2.3 GiB 的完整鏈 cache 並保持 5 GiB 停止線及餘裕；不是判定 CoreML 不支援。**Windows DirectML 與 iOS 均未跑**。Mac CPU 結果與先前不同 checkpoint 的 CoreML 結果不可合併成三平台已驗證。

### 決定、失敗紀錄與重現

**本輪實驗完成，產品採用仍 NO-GO。** 新場景權重有真實原生 CPU 單步資格，但沒有解除畫質反例或完整鏈 strict fail，成本也未提供產品採用理由。先停在這組 checkpoint／設定的小樣本前測，不把 T1 未執行項勾成完成，不進 T2–T5，也不以小洞子功能、古典 fallback 或放寬容差偷換通用移除需求。

下一步不是再改 mask／seed 挑漂亮結果：只有具名且權利可追溯的另一候選，先在同一批反例消除顯眼新結構並顯示必要品質收益，才值得擴至原 T1 開發集，投入精確原生數值／貼回與 full-frame／平台工程。24 張 T5 留出集保持未動；改 checkpoint、VAE、exporter、ORT 或資料政策即重新取相應資格。產品 owner 決定是否繼續候選比較，散布／訓練 lineage 仍須相應人類審查；沒有藉本次研究自動新增大型模型或發布。

證據位於忽略目錄 `dist/object-removal-places2-20261020/`：`frozen-inputs.json`、`student/result.json`、`student/checkpoint-comparison.json`、`student/onnx/cases.json`、`vae/result.json`、`vae/per_tensor.json`、`vae-numpy-verification.json`、`native-runs.json`、`native-summary.json`、`numeric-verification.json`、`visual-inspection.json` 與 `paired-contact.png`。實際 profiler、raw tensor、逐次命令／RSS／錯誤留在該目錄；不隨研究文件發布。

保留的 harness 失敗亦未洗掉：VAE 任務的預期 SHA 少寫首字元，原 mismatch receipt 保留，協調者另由固定 HF LFS 與本機串流 hash 核對完整值；中段 timestep 初次索引斷言錯誤在執行交叉比對前修正；macOS `ru_maxrss` 的舊欄位單位誤標由明確 bytes／GiB 記錄解釋。新宿主亦讓 mask guard 僅反映 mask 比對，不再因較早 VAE fail 誤報 mask 不一致；原始 fail receipt 不修改。

執行來源與 manifest 封存於 `verification-harnesses.tar.gz`，不包含模型、照片、raw tensor 或 cache；重現需保留既有 venv、上述模型／輸入與舊 VAE 圖，先解開至原忽略目錄。腳本保留本機路徑；搬機需重綁路徑，不能把這份重現指令當成跨平台驗收：

```bash
ROOT=dist/object-removal-places2-20261020
PY=dist/object-removal-feasibility-20261020/venv/bin/python
tar -xzf "$ROOT/verification-harnesses.tar.gz" -C "$ROOT"
TORCH_FORCE_WEIGHTS_ONLY_LOAD=1 "$PY" "$ROOT/verify_vae_numpy.py"
"$HOME/.dotnet/dotnet" build "$ROOT/native/native-runner/NativeRunner.csproj" -c Release --disable-build-servers -p:UseSharedCompilation=false
"$HOME/.dotnet/dotnet" build "$ROOT/native/MoebiusChain/MoebiusChain.csproj" -c Release --disable-build-servers -p:UseSharedCompilation=false
"$PY" "$ROOT/run_native.py"
"$PY" "$ROOT/inspect_results.py"
"$HOME/.bun/bin/bun" "$ROOT/verify_documents.mjs" "$PWD"
```

## 13. 多角色替代方案與實驗

### 範圍、證據中斷與資料版本

使用者要求繼續調查其他模型／方案，以多角色平行進行 evidence-based Socratic critical thinking，之後實驗並提交文件。本輪只更新五份研究文件並作本機隔離實驗；**不 push、不實作產品、不上傳照片、不呼叫付費服務，也不把 Clone／Telea 當作隱藏 AI fallback**。

四個角色完成來源調查後，工作目錄出現外部變更：整個 `dist/` 已不存在，可用空間由約 6.2 GiB 變為 29 GiB；產品來源與五份文件仍在。前述模型、venv、原圖、收據及本輪 v1 材料因此不可讀。既有章節保留當時觀察，不追改失敗；**其原始 artifact 現已缺失，不能把歷史數字當作本輪可重新核對的輸出**。只從仍存的記憶體 metadata 恢復來源雜湊與 v1 清單，未復原或重跑舊模型。記錄見 `workspace-interruption.json`。

重新建立的 **v2 是新資料，不是舊測試的重跑**：

- 四個公開 NIND JPEG 縮圖：droid ISO 200／6400、磚牆、自行車，共 **3 個攝影場景**，另有 2 個確定性 RGB16 梯度／線條表面。
- 共 16 個 512×512 案例：12 個有原圖參考的洞內重建、4 個沒有唯一乾淨背景參考的實際移除。所有演算法的洞內 conditioning 先歸零；原圖只供評估，donor 必須完全落在已知區。
- 使用 Commons 明列的 960px 縮圖與新 crop／mask；首次原始磚牆檔請求 HTTP 429，未重試該請求。不能與第 9–12 節全解析度裁片直接比較，也不能用縮圖判定完整感光雜訊保存。
- 攝影資料本身是 RGB8 JPEG；浮點或 RGB16 儲存不增加原始資訊。只有另製的程序化表面含真正非 8-bit 量化值；非均勻 alpha 也是合成測試，不是產品透明影像、RAW 或 HDR 驗收。
- 作者 Trougnouf（Benoit Brummer）；頁面授權 CC-BY-4.0，與 EXIF 的 CC-BY-SA-4.0 差異並列保存。照片及衍生輸出不隨文件提交。輸入 SHA-256 在推論前凍結；24 張 T5 留出集未使用。

### 平行角色與主張裁決

實際啟動四個並行 scout 工作；沒有宣稱呼叫特定外部模型，也沒有把角色共識當成實測。原始建議中的過度主張由協調者核對第一手來源後裁決，修正意見不是第二份獨立實驗。

| 角色 | 質疑與可回答的證據 | 採用與不同意之處 |
|---|---|---|
| `CompactLearnedLens`：小型學習式重建 | 小檔案、Apache 原始碼是否足以證明精確權重可用？需具名權重 grant、來源位元組對應及實際圖／照片結果 | 採用 AOT 的候選線索；不同意以原始碼授權＋HF 鏡像直接核准 checkpoint。ZITS 多檔大小未知，不刊登估算總和 |
| `GenerativeRouteLens`：生成式與完整鏈 | one-step 或小型 LoRA 是否省去大型 base？檢查必需 artifact 的真實 bytes，而非只數 adapter | 採用 OSOR 新路線與逐元件條款；捨棄未證實的整鏈 GiB 總數、無法解析的 pin 與「所有路線授權失敗」說法 |
| `ClassicalInteractiveLens`：古典與 donor | 不生成新物體，是否就等於照片自然？用相同 mask 實跑、保留紋理／結構反例及位元深度代價 | 採用顯式研究對照；捨棄未量測毫秒／記憶體保證、品質上下界與專利到期算術所導出的法律結論 |
| `WorkflowEvidenceLens`：操作與評估 | 沒有通用本機模型，是否仍有誠實的手動／外部工作流？需真實 API 契約、使用者成本與資料外送邊界 | 保留工作流研究；把「零公開 SDK」改為本輪未建立整合證據，不把 staging 範例、模型宣傳或修圖結果保存當成產品已支援 |

最強反對意見是：古典對照沒有回答「通用 AI 移除」需求。裁決接受此限制；它們只檢驗速度、格式、可保存合成與失敗形態，**不能解除 T1，也不能據此縮減原需求**。另一個重要反例是：高 PSNR 的平滑填補可能很不自然，合理但不同的背景也可能低 PSNR；真實移除不計算相對於被刪物體原圖的 PSNR。

### 候選採用帳本

以下 `adopted` 只指**本輪研究對照**；沒有產品模型或功能被採用。`deferred` 不表示所有裝置不支援，也不是法律意見。保留最初 700 MiB 新材料上限作本輪研究限制，不把它寫成永久產品上限；實際超額另見下方。

| ID | 方案與第一手證據 | 狀態 | 理由與重新開啟條件 |
|---|---|---|---|
| A01 | OpenCV 4.10 Telea／Navier–Stokes，[來源／格式契約](https://raw.githubusercontent.com/opencv/opencv/71d3237a093b60a27601c20e9ee6c3e52154e8b1/modules/photo/src/inpaint.cpp)、[Apache-2.0](https://raw.githubusercontent.com/opencv/opencv/71d3237a093b60a27601c20e9ee6c3e52154e8b1/LICENSE) | adopted：顯式對照 | 已實跑；三通道只支援 RGB8，單通道另支援 uint16／float32。分平面與數值尺度不是無成本的等價替代；不引入產品 Python／OpenCV runtime |
| A02 | 指定來源直接複製、[OpenCV Poisson cloning 契約](https://raw.githubusercontent.com/opencv/opencv/71d3237a093b60a27601c20e9ee6c3e52154e8b1/modules/photo/include/opencv2/photo.hpp) | adopted-partial：人工來源對照 | 固定 donor 位移、3 個小洞，另跑 .NET RGB16。Poisson API 是三通道 RGB8，不能宣稱原生 RGB16；人工 donor 不等於自動選取或通用重建 |
| A03 | [AOT-GAN 原始程式](https://github.com/researchmm/AOT-GAN-for-Inpainting/blob/2cd1afd8fdfabb101c678f6062d14bc7d302509e/src/model/aotgan.py)、[官方釋出資料夾](https://drive.google.com/drive/folders/1bSOH-2nB3feFRyDEmiX81CEiWkghss3i)、[HF artifact 清單](https://huggingface.co/api/models/NimaBoscarino/aot-gan-places2/tree/efa10114468c163df0ba650d6af5681cec6ba154) | deferred：精確權重資格未閉合 | HF `pytorch_model.bin` 為 60,846,567 bytes，LFS SHA-256 `bccbee30d45e8682abfa803f2c7b1268a637d5b9d7f5fb0048765ffd542e8843`。已讀 generator 的卷積／dilation／resize／normalization，未匯出圖。原始碼 Apache-2.0 不補出鏡像的獨立權重 grant 或官方檔逐位元 lineage；本輪不下載 |
| A04 | [ZITS 第一手 pipeline](https://github.com/DQiaole/ZITS_inpainting)、其連結的 ZITS++／LSM-HAWP／SSU／LaMa | deferred：多元件與精確材料未知 | 不是一個小型 structure token 就完成填補；需結構偵測／恢復、upsampling、texture restorer。完整 checkpoint bytes、權利、FFT 匯出與原生數值仍須逐件核對；不把 training CUDA 指令直接當推論不支援證據 |
| A05 | [FcF 來源](https://github.com/SHI-Labs/FcF-Inpainting)、[含 NVIDIA 例外的 LICENSE](https://github.com/SHI-Labs/FcF-Inpainting/blob/main/LICENSE) | deferred：權重／相依／匯出資格 | 不能從 Apache badge 清除第三方條款，也不能因含 CUDA 元件就斷言沒有 CPU reference。未取得或執行 `.pkl`，完整大小、精確載入安全與 portable graph 未建立 |
| A06 | [OSOR pin](https://github.com/Zhouqm-Git/osor/blob/d5b68e822996d61aae6ba612e5e9b5a14f576879/README.md)、[模型 pin／bytes](https://huggingface.co/api/models/QinmingZhou/OSOR/tree/9288156766b1df44e5f50fb697876a6f2fd8c062/osor-sdxlinpainting/weights) | deferred：完整 base／材料及契約 | SDXL Phase II 單檔即 3,112,296,806 bytes，另需 base。code MIT、SDXL checkpoint Open RAIL++-M、FLUX checkpoint NC 分開。作者宣稱 one-step／effect-aware，非本機品質證據；預設不 paste-back、alpha 可擴到原 mask 外，需先解決接受區域與保護背景契約 |
| A07 | 第 11 節 S20 PowerPaint v2.1、S21 BrushNet 的具名 checkpoint 與 base | deferred：完整鏈材料 | 既有清單的 3,347,074,737-byte UNet、2,475,354,520-byte BrushNet 分支各自已大於本輪上限；不另編完整鏈總量。沒有新下載、推論或 Mac／Windows／iOS 能力結論 |
| A08 | [原作者 PatchMatch 發布頁](https://gfx.cs.princeton.edu/pubs/Barnes_2009_PAR/)；exemplar／Criminisi 類 | study／deferred | 原作者包是 noncommercial research-only 的 MATLAB mex matching core，**不包含 synthesis application**。第三方 MIT wrapper 不自動清除整鏈權利。未作專利有效性、侵權或商用 clearance 判定；本輪不以簡化 donor copy 冒稱 PatchMatch |
| A09 | [OpenCV contrib FSR／ShiftMap 契約](https://raw.githubusercontent.com/opencv/opencv_contrib/4.10.0/modules/xphoto/include/opencv2/xphoto/inpainting.hpp) | deferred：本輪未安裝 contrib | `xphoto` 的 non-zero mask 代表有效區，與 `cv::inpaint` 相反。未增加另一套套件或跑 FSR，沒有其品質／速度結論 |
| A10 | 明確指定外部編輯器的 raster handoff；Apple Photos Clean Up | study：未建立嵌入／回傳閉包 | 本輪未建立公開 Clean Up 整合介面證據，非宣告所有 SDK 永遠不存在。外部編輯器的授權、遙測、mask／色彩與再匯入各有邊界；啟動程式不等於已完成可重開的非破壞編輯 |
| A11 | [Adobe Fill Masked Areas 指南](https://developer.adobe.com/firefly-services/docs/photoshop/guides/using-fill-mask/)及 S17 Vertex API | deferred：無外送／付費授權 | Adobe 指南示範 cloud URL、credentials、非同步回應及 staging base；不推定 production endpoint、費用或 16-bit 契約。沒有請求生成、上傳圖片或把 mock payload 當整合完成 |

### 本機實驗與可反證結果

環境是 Apple M3／16 GiB、新建**隔離研究** Python 3.11.17、OpenCV `4.10.0.84`、NumPy `1.26.4`、Pillow `11.3.0`；OpenCV 使用單執行緒。沒有修改全域套件或產品相依。OpenCV tag `4.10.0` 由 GitHub annotated tag 解到 commit `71d3237a093b60a27601c20e9ee6c3e52154e8b1`；不沿用 advisory 的錯誤 SHA。

先凍結 16 案 × 2 演算法 × 3 模式共 96 組，radius 3：RGB8、逐平面 float `[0,1]`、逐平面 float `[0,65535]` 後縮回。另有 3 個小洞各做硬複製、normal／mixed Poisson 共 9 組。donor 位移固定 `(-160,0)`，在看輸出前依幾何檢查沒有洞內或越界取樣，未依畫質挑選來源。

初次 6 組 Poisson 對照的目的區歸零導致黑塊；這是 **harness 初始化反例，不是 Poisson 家族的品質判決**。保留原輸出，另以相同已知 donor 預填目的區，新增 6 組具不同名稱的 normal／mixed 結果，不借用被遮住的原始像素。總計 111 組保存輸出，其中 6 組舊初始化結果不計入可用演算法品質比較。

| 研究模式 | 案例數 | 各案 warm p50 範圍，ms | 實測邊界 |
|---|---:|---:|---|
| Telea RGB8 | 16 | 3.90–31.20 | 含輸入／輸出轉換，不把完整原圖降為 RGB8 保存 |
| Telea float `[0,1]` | 16 | 4.71–65.09 | 共有 1,777,868 個洞內色彩元素越界；保留 raw，再明示 clip，不能以有限值／PNG 成功稱其正確 |
| Telea float `[0,65535]` | 16 | 4.63–65.19 | 沒有上述越界，但不因此等價於 RGB8 或適合物件移除 |
| NS RGB8 | 16 | 3.44–35.69 | 只證明此組模式／輸入的實際成本 |
| NS float `[0,1]` | 16 | 3.30–76.87 | 與 RGB8 路徑不能假定逐像素相同 |
| NS float `[0,65535]` | 16 | 3.17–61.57 | 不外推 full-frame 或行動裝置 |
| Python donor 硬複製 RGB16 | 3 | 0.91–0.96 | 有額外人工來源；不是自動填補 |
| 修正初始化的 normal Poisson RGB8 | 3 | 5.05–5.22 | 含 source／destination 預填的明確研究契約 |
| 修正初始化的 mixed Poisson RGB8 | 3 | 5.03–8.13 | 未解除來源錯位與接縫反例 |

每案五個 warm 樣本；這是**各案 p50 的範圍**，不是整體 p50、冷載入或 p95 保證。PDE 計時含模式轉換與平面拆合；Poisson 計時為已準備 source／destination 的 cloning 呼叫與輸出轉換。均不含程序 import、檔案 I/O、RGB16 合成、PNG、產品 UI。初次整個 Python wave 的 `/usr/bin/time -l` max RSS 為 124.67 MiB，不能當每個演算法、每個候選或完整產品的峰值。

協調者直接看過 `removal-contact.png`、`structure-contact.png`、`donor-contact.png`：

- 大磚牆洞形成放射／拖曳狀色帶，磚縫沒有被合理重建；小洞與細線也可能留下接縫。沒有因不生成新物體就變成自然照片。
- 中央 droid 被抹去後，出現牆面／地面的色彩延伸；自行車架區域仍有破碎輪框與背景痕跡。粗略 mask 與遮擋歧義保留，不冒稱語意選取精度測試。
- `[0,1]` Telea 的灰／高振幅結果是範圍陷阱，不用 clipping 掩飾為成功。較高數值尺度避免這個反例，並不補回大洞語意。
- donor 保留真實紋理卻可能錯位，程序化漸層出現顯眼色差；修正 Poisson 黑塊不等於自動找到正確來源。這些是小樣本觀察，不是三平台或所有古典方法的永久上限。

已知答案的洞內重建也不能冒充移除排名：

| NS RGB8 的已知答案案例 | 洞內 PSNR，dB | 可支持的判讀 |
|---|---:|---|
| 程序化平滑梯度小洞 | 41.23 | 此表面容易被邊界延伸近似 |
| 磚牆大洞 | 11.54 | 同方法在有結構／紋理的大洞明顯失敗 |

四個真實移除案例沒有這種 PSNR 分數；沒有進行人類盲評，也沒有與已遺失的舊模型輸出做假同場比較。

### 保存、原生邊界、失敗與重現

全部候選只在 mask 內量化／貼回，mask 外 RGB16 與全部目的 alpha 沿用原始 bytes。另用 OpenCV decoder 讀回 **111 個 RGBA16 PNG**，以凍結原始資料及 gzip raw tensor 重新核對：未發現洞內量化、mask 外 RGB 或 alpha 不一致，所有 raw 候選皆有限。反向控制將**整張**程序化 RGB16 經 RGB8 往返，確實改變 745,291 個洞外色彩元素；所以「保留原圖外部」不是空測試。

另建置並執行真正 .NET 10 CLI，對相同 3 組 donor 做直接 RGB16 複製；建置 0 warnings／0 errors。逐組獨立 oracle 各比較 1,048,576 個 RGBA16 samples，皆零差異；越界 donor 被拒絕、無成功輸出。單次內部 copy 約 0.148–0.158 ms，程序 max RSS 約 57.97 MiB；這個計時排除初始化／驗證／I/O，**不能拿來直接對比上表 Python 呼叫成本**。僅證明這個 Mac probe，不證明 OpenCV 已接進 .NET、產品生命週期、Windows 或 iOS。

研究儲存量也有失敗：初次 wave 後實測約 **833.43 MiB，超過事前 700 MiB 估算**，沒有宣稱全程守限。停止擴大模型研究，將本輪 candidate tensors 逐檔 gzip，解壓後核對 SHA-256 才移除自己的未壓縮副本；原始壞結果與 receipt 均保留。修正後資料約 637.67 MiB；5 GiB 可用空間停止線未被觸及。重現腳本仍需事先預留輸出／壓縮暫存，原始 runner 不是可靠的硬配額隔離器。

目前證據只在忽略目錄 `dist/object-removal-alternatives-20261020/`：`panel-adjudication.json`、`workspace-interruption.json`、`photograph-acquisition-v2.json`、`inputs-v2.json`、`experiment-selection.json`、`execution-wave.json`、`results.json`、`poisson-correction.json`、`tensor-storage.json`、`verification.json` 與三份 contact PNG。原始腳本封存於 `verification-harnesses.tar.gz`，不包含 venv、照片或 candidate tensors；驗證依賴現存資料，不能只靠壓縮包重現舊章節：

```bash
ROOT=dist/object-removal-alternatives-20261020
PY="$ROOT/venv/bin/python"
# 在同一工作目錄恢復已封存的本輪驗證來源；不重跑舊模型失敗。
tar -xzf "$ROOT/verification-harnesses.tar.gz" -C "$ROOT"
"$PY" "$ROOT/verify_outputs.py"
"$HOME/.bun/bin/bun" "$ROOT/verify_documents.mjs" "$PWD"
```

公開攝影來源：[NIND dataset](https://commons.wikimedia.org/wiki/Natural_Image_Noise_Dataset)、[droid ISO200](https://commons.wikimedia.org/wiki/File:NIND_droid_ISO200.jpg)、[droid ISO6400](https://commons.wikimedia.org/wiki/File:NIND_droid_ISO6400.jpg)、[磚牆](https://commons.wikimedia.org/wiki/File:NIND_wall_ISO200.jpg)、[自行車](https://commons.wikimedia.org/wiki/File:NIND_threebicycles_ISO200.jpg)、[CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/)。crop、遮罩、合成 alpha、取代洞內容及修補皆屬本輪變更，不暗示作者背書。

**決定：本輪 Survey／對照完成，產品通用移除與 T1 仍 NO-GO。** 沒有為了做出功能而暗換古典方案，也沒有將 source-level 可移植性推測當成模型通過。下一個學習式實驗先閉合具名權重／base 的用途資格與完整材料清單，再在預先固定的開發資料比較；若選 OSOR，還須明訂擴張接受區域的操作語意。有效改善後才進原 T1 品質／原生／平台資格；T2–T5 不因本輪對照自動啟動。

## 14. 小型模型與 Depth 分層實驗

### 決定與範圍

使用者要求接續小型模型重建與 Depth 拆解圖層。本輪只做本機研究、五份既有研究文件更新及忽略目錄 probe；不新增產品程式、永久測試、runtime 或 UI，也不 commit／push。**Depth Small 留作相對深度／可見分層研究；MI-GAN FP32 留工程對照；FP16 儲存衍生檔不採用。沒有新合格的重建模型家族，通用移除／T1 仍 NO-GO。**

資料先凍結再推論：沿用第 13 節 v2 的 16 個 512×512 案例，包含三個攝影場景的四張 JPEG 縮圖及兩個程序化高精度表面；其中 12 個為已知答案的挖洞重建，四個為沒有隱藏背景真值的物件／陰影移除。四張照片不算四個獨立場景，16 案例不算 T1 開發集或 T5 留出集。核對四張來源及 80 個凍結檔案；未接觸 T5。Depth 另加一個明確標示的合成遮擋控制，不把它當攝影品質或實測距離。

| 路線 | 本輪可取得的證據 | 處置 |
|---|---|---|
| AOT-GAN | 再查第一方程式碼／下載入口與公開鏡像；仍未閉合精確權重的用途 grant／lineage，鏡像 license badge 不補出第一方權重授權 | blocked，未下載／執行權重，不宣稱架構失敗 |
| EdgeConnect | [第一方 README](https://github.com/knazeri/edge-connect#license) 的 CC-BY-NC 內容與 checkpoint 授權範圍仍未閉合；下載入口沒有本輪可固定的完整 artifact 身分 | blocked；不是將研究用 NC 一概當作不可用，也不推定可散布 |
| MI-GAN FP32／FP16 儲存版 | 重新取得有明確 MIT 權重聲明的 MI-GAN，從官方 wrapper 擷取原 float32 core；另做 FP16 initializer 儲存、FP32 計算衍生檔 | 顯式既有模型對照，不冒稱新模型；衍生檔數值失敗 |
| Depth Anything V2 Small | [官方授權說明](https://github.com/DepthAnything/Depth-Anything-V2#license) 與[第一方 HF artifact](https://huggingface.co/depth-anything/Depth-Anything-V2-Small-hf/tree/5426e4f0f36572d16453bbda7a8389317b1bef99)；實際推論、ONNX 匯出及 .NET CPU 執行 | 留研究；不是分割、alpha matting 或隱藏背景生成 |
| Qwen-Image-Layered | [作者說明](https://github.com/QwenLM/Qwen-Image-Layered/blob/54c4fe47e76d745775e03fc66ee38457280ed9ea/README.md) 是生成式多 RGBA 分解，不是 Depth 切帶；[固定版本 transformer 清單](https://huggingface.co/Qwen/Qwen-Image-Layered/tree/8f0ca708dfff6ba1dd5f2d85d78f8c108a040bcf/transformer) 五個 shard 合計 40,861,040,688 bytes，尚未含 text encoder、VAE 與 runtime | 原始檔案已超過本輪空間／3 GiB 材料上限，未下載／執行；量化版本的身分、保真、資源與完整權利未知 |

以上是研究用途資格與本輪可執行性的判斷，不是法律意見、全鏈散布核准或模型家族排名。AOT／EdgeConnect 的原始來源與雜湊留在 `inpaint/rights/`，不以被阻擋的模型換取「已完成新模型比較」的說法。

### 小型重建與儲存精度

MI-GAN core 的輸入是 `[1,4,512,512]` float32：已知區 mask 加上遮掉洞內容的 RGB；原圖乾淨洞內容只供評估，不進模型。擷取沿用原 ONNX 節點與 initializer，沒有自造 operator；本輪 core digest 與舊章節重新匯出的檔案不同，不假定 bytes 相同。補齊全部 16 案例：先前八個 raw 結果經雜湊核對後重用，另八個實際推論。

有一個**測試工具失敗**：初次 `Image.fromarray(uint16 RGBA, mode='RGBA')` 寫出的八個 PNG，IHDR 實際為 8-bit，contact 甚至呈黑色。原始壞檔與失敗收據保留；另以明確 PNG16 encoder 從 raw 產生 16 個新 RGBA16 PNG，獨立解碼後核對每張 1,048,576 個 samples：洞內量化、洞外 RGB 與全部目的 alpha 均符合凍結 oracle。部分模型 raw 洞內超出 `[0,1]`，保留逐案 clipping 數，不把裁切後有限值當原始輸出沒有越界。

實際查看修正後 contact：小洞可補出近似局部紋理；大面積壁紙／磚牆會出現顯眼新結構，自行車目標仍有殘留。這些是同一 v2 資料上的可見反例，不是人類盲評或全家族結論。PNG encoder 修正不消除模型品質失敗。

另一個實驗將 155 個 initializer 存成 FP16，再 Cast 回 FP32 計算；**不是原生 FP16 推論，也不是新模型家族**。以下量測皆為相同 Apple M3／16 GiB、.NET 10／ORT `1.25.0`、CPU 2 threads、opset 17 的 probe；各 artifact 兩個不同輸入，每例三次 warm Run，區間是兩例各自 p50／程序 max RSS：

| artifact | ONNX bytes | warm p50（ms） | 程序 max RSS（MiB） | 原門檻數值比較 |
|---|---|---|---|---|
| MI-GAN FP32 core，512×512 | 29,565,521 | 294.7–299.0 | 984.1–984.4 | 兩例通過；僅與同 FP32 graph 的 Python ORT 對照，不是獨立 PyTorch 模型保真 |
| MI-GAN FP16 儲存／FP32 計算 | 14,850,876 | 293.2–295.3 | 981.4 | 兩例失敗；paint-small／wall-large 分別有 117,476／194,171 個元素越界，max abs 為 0.04185／0.08603 |
| Depth Small backbone，518×518 | 99,179,616 | 644.6–648.4 | 472.7–472.8 | 兩例均與重新載入權重所得的 PyTorch 參考通過 |

門檻固定為 `abs(candidate-reference) <= 0.0002 + 0.002*abs(reference)`，沒有放寬；六組 native 輸出均無非有限值。FP16 檔案縮小近半，卻沒有與之相應的 RSS 下降或可採信的速度改善，且未保持原數值契約，故不採用。ORT constant folding 可能把 Cast 後權重還原成記憶體中的 FP32，不能用下載大小推定運算記憶體。計時排除 decode、ROI、PNG、產品合成與 UI；RSS 包含整個 probe，不是 activation-only。profile 實際只見 `CPUExecutionProvider`；未測 CoreML、Windows 或 iOS。

### Depth 與可見圖層

只用 Small 的 Apache-2.0 權重範圍，不延伸到官方另列 CC-BY-NC 的 Base／Large／Giant。HF pin 為 `5426e4f0f36572d16453bbda7a8389317b1bef99`；`model.safetensors` 為 99,173,660 bytes、SHA-256 `3152477ce0d8d6978d76b995120de97cb5b928701fd0f817769f59e249a16b70`，實際參數 24,785,089。四張照片與合成控制均真實執行 CPU float32 推論。

攝影分層路線使用 HF／Transformers `4.49.0` 的 Pillow bicubic、保持比例／14 倍數輸入，再 bicubic `align_corners=False` 放回照片尺寸；**不是**官方 OpenCV resize／bilinear `align_corners=True` 路線。native 匯出則是另一個固定 518×518 方形 backbone 輸入，前後處理不在 graph；兩個參考在同一方形輸入上各自重新執行 PyTorch。不能把上表 native 成本或數值通過說成整條保持比例分層鏈已在 .NET 執行。攝影 HF 推論程序曾達約 2,377 MiB RSS，小權重不代表整個 Python 環境只用 100 MB。

分層只依事前固定的 33／66 百分位，把**可見像素**分到互斥 far／mid／near 三個 RGBA layer；相對 inverse depth 較大稱 near，不宣稱公尺或校準距離。照片圖層本來就是 JPEG 8-bit 來源，沒有偽稱提高感光精度；合成控制另用真正 RGBA16 來源及三張 PNG16 圖層。獨立讀回五組圖層，都能精確重組原可見影像；RGBA16 控制的 614,400 個 samples 零差異。

但這**不是實例分割**：droid contact 的同一 near band 包含機器人、下方石礫及壁紙部分花樣；合成場景中的同一已知背景也被分進三個 band。這不能取代人工選取／SAM 提案，更不是髮絲 alpha。相同深度的不同物件、陰影、倒影與透明物件仍沒有相應物件身分或遮擋真值。

將 near layer 固定右移 32 px 後，只保留仍被已知 layer 覆蓋的像素，其餘輸出明確 unknown／透明，不用 inpaint 或底圖偷補。獨立重算的未知覆蓋如下；RGBA16 控制的移動結果也與獨立 oracle 零差異：

| 案例 | 新露出的 unknown pixels |
|---|---|
| wall | 20,512 |
| bikes | 50,748 |
| droid ISO200 | 30,985 |
| droid ISO6400 | 29,258 |
| 合成遮擋 | 10,951 |

另建一個**非可辨識性控制**：兩個不同的隱藏背景，因被同一前景遮住而形成完全相同的觀測圖；移動遮擋物後，4,096 個新露出像素的正確值可不同。此處不是模型評分，而是證明「看見深度／可精確重組」不足以決定看不見的背景。真正 amodal／生成式圖層仍需合成未知內容，並標示生成來源與品質限制；不能把 Qwen 的作者能力宣稱當成已在本機量測。

### 證據、重現與下一步

目前證據在忽略目錄 `dist/object-removal-compact-depth-20261020/`：`frozen-inputs.json`、`inpaint/result.json`、`inpaint/completed/result.json`、`depth/result.json`、`layer-route-survey.json`、`native-runs.json`、`native-summary.json`、`numeric-verification.json`、`preservation-verification.json`、`occlusion/result.json`，以及修正後重建／各 Depth contact。完整 ONNX、輸入與參考 SHA-256 各留在 native manifests，raw tensor 與先前八張壞 PNG 保留。錯誤模型 digest／遺失模型兩個負向控制均在成功報告前拒絕。

恢復 smoke 發現初版 native probe 在啟用 profiling 後才設定 prefix，原 profile 落到工作目錄；七份原始 profile（六組量測加首次恢復）已逐檔雜湊核對並移存 `profiles-original/`，原收據不改寫，對照見 `profile-relocation.json`。量測來源原版另留 `verification-harnesses-initial-profile-order.tar.gz`；主封存只修正 prefix／enable 順序，再做獨立恢復 smoke，沒有重跑或追改兩項 FP16 失敗。

執行來源及環境清單封存於 `verification-harnesses.tar.gz`，來源雜湊與恢復 smoke 見 `source-manifest.json`／`archive-verification.json`。壓縮包不含 venv、模型、照片或 tensor；以下需現存 frozen data 與模型，不是跨機器安裝器，也不會補出第 13 節前已消失的證據：

```bash
ROOT=dist/object-removal-compact-depth-20261020
PY="$ROOT/venv/bin/python"
tar -xzf "$ROOT/verification-harnesses.tar.gz" -C "$ROOT"
"$PY" "$ROOT/verify_preservation.py"
"$HOME/.bun/bin/bun" "$ROOT/verify_documents.mjs" "$PWD"
```

```bash
# 此檢查預期退出 1，保留 FP16 儲存版的兩項數值失敗；不是全綠收據。
"$PY" "$ROOT/verify_numeric.py"
```

本輪新目錄完成量測時約 1.46 GiB，低於事前 3 GiB 材料上限；觀察到的可用磁碟約 26 GiB，未觸及 8 GiB 停止線。這是觀測與程式 guard，不是硬配額隔離或未量測的峰值承諾。

**下一個可檢驗方向**：Depth 可作使用者確認的可見範圍／遮擋提示，不當成自動物件 mask；若目標是刪除／移動物件後得到完整圖層，需先閉合具名小型生成／amodal artifact 的權利、資源與未知像素契約，再作同輸入比較。AOT／EdgeConnect 缺的仍是精確權重資格／來源，不能靠再跑 MI-GAN 補足。研究證據完成不等於產品通過；既有修補 stage、接受像素保存與 T1–T5 gate 不變，未啟動產品圖層編輯器。

## 15. 提示詞與 LLM 引導實驗

### 問題、決定與可推翻範圍

2026-10-21 接續使用者的問題：「允許加入提示詞，會不會大量改善？」本輪執行本機對照，而非只列模型或以不同模型的輸出推論提示詞效果。**在實測的 SD 1.5 inpainting artifact／設定上，兩輪均沒有看到攝影重建的大幅改善；文字確實能改變部分內容，卻沒有使兩個自然移除案例得到可信的移除。** 這不否定其他 instruction model，也不把兩個攝影場景的小幅負中位差解讀為提示詞普遍有害。

本輪只更新既有研究文件與忽略目錄證據，不新增產品功能、永久測試、正式相依套件或 UI，不 commit／push。**T1 仍 NO-GO，T2–T5 未開始。** 提示詞不改變接受遮罩、逐張像素資產、RAW／顯示域、undo／重開或原生平台資格。

### Survey：文字、工具規劃與評審是不同機制

| 機制／原始來源 | 真正改變的部分 | 本輪證據與處置 |
|---|---|---|
| [SD 1.5 inpainting 固定鏡像](https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-inpainting/blob/8a4288a76071f7280aedbdb3253bdb9e9d5d84bb/README.md) | CLIP 文字條件進入具有 mask／masked-image 輸入的 denoiser；不等於一般聊天模型執行「刪除」命令 | 實際執行；空提示與文字提示共用同一 artifact、取樣與合成，才可比較提示詞效果 |
| [PowerPaint 論文](https://arxiv.org/html/2312.03594v1)、[固定作者 README](https://github.com/open-mmlab/PowerPaint/blob/5b4c3d52291709fcec2a1870d987da693fd3549c/README.md) | 經訓練的 task prompt／embedding 區分 context 與 object；不是對任意現成模型輸入相同拼字就得到相同能力 | study／reproduce 候選；未執行，完整模型／base／文字元件條款與數值仍須閉合，不把程式碼 MIT 當全鏈 grant |
| [Grounded-SAM 固定來源](https://github.com/IDEA-Research/Grounded-Segment-Anything/blob/126abe633ffe333e16e4a0a4e946bc1003caf757/README.md) | 文字定位、分割與填補是不同工具；LLM 可以提出結構化意圖，但選取結果仍須檢查 | study；本輪沒有自動產生新 mask／ROI，不能宣稱節省人工或分割變準 |
| [MGIE](https://github.com/apple-aiml-research/ml-mgie/blob/c21037006c1013bdb09958a4a96c0f0ccad8b79a/README.md)、[SmartEdit](https://github.com/TencentARC/SmartEdit/blob/a65f1262dfcba68c138ea95fe9936df1bd2c111d/README.md) | 訓練過的多模態理解與影像編輯模型聯結，不是把聊天輸出直接接到既有 MI-GAN | study；未下載／執行；MGIE weight-differential 的 NC 文字與 SmartEdit 全鏈資格／實測資源分開處理 |
| [Inst-Inpaint](https://github.com/abyildirim/inst-inpaint/blob/a934ba0066b466b58d8430ad28c7b6b9316caa31/README.md)、[InstructPix2Pix](https://github.com/timothybrooks/instruct-pix2pix/blob/0dffd1eeb02611c35088462d1df88714ce2b52f4/README.md) | instruction-aware 訓練；LLM 也可能只參與離線指令改寫／訓練配對，與每次推論都呼叫 LLM 不同 | study；來源查核，不是本輪已測的方法或速度／畫質保證 |
| [VIEScore 固定來源](https://github.com/TIGER-AI-Lab/VIEScore/blob/2cbea4e8ce5721425a8772590ecdbe7d2edd1721/README.md) | 分開檢視語意遵從與感知品質，可啟發有限候選的 critic／ranking | study；沒有啟用雲端評審或以模型分數代替人類／像素 oracle；本輪 critic 只收到文字觀察，不看影像 |

以上作者範例／benchmark 仍是作者宣稱，沒有因讀到原始來源就變成本機複現。來源版本、README SHA-256 與完整角色報告留在 `survey-sources.json`、`primary-source-pins.json`、`provenance/` 及 `panel/`。既有 MI-GAN core 只有 known-mask＋RGB `[1,4,512,512]`，沒有文字輸入；不能加一個 prompt 欄位就宣稱它接受文字條件。

另外查核了較新的原生 image-editing 候選，不將舊 SD 模型的結果當作全家族結論：

| 僅來源查核的候選 | 固定 revision | canonical 文字／生成／VAE 權重 bytes 合計 | 界線 |
|---|---|---|---|
| [FLUX.2-klein-4B](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B/blob/e7b7dc27f91deacad38e78976d1f2b499d76a294/README.md) | `e7b7dc27f91deacad38e78976d1f2b499d76a294` | 15,964,212,614 | 原始權重已超過本輪新目錄 8 GiB 材料上限；未下載／執行 |
| [Qwen-Image-Edit-2511](https://huggingface.co/Qwen/Qwen-Image-Edit-2511/blob/6f3ccc0b56e431dc6a0c2b2039706d7d26f22cb9/README.md) | `6f3ccc0b56e431dc6a0c2b2039706d7d26f22cb9` | 57,699,249,798 | 同上；不是本輪實測失敗，也不是 Qwen-Image-Layered |

合計來自固定 HF tree 的 canonical safetensors，不是峰值 RAM、GPU 常駐量或全鏈安裝大小，亦未含設定／tokenizer／runtime。兩份 card 宣告 Apache-2.0 不等於本文件核准全鏈散布。量化／轉換版是需另外固定來源、保真、完整元件與資源的 artifact；不能用原始檔案上限拒絕整個模型家族。

### 固定對照與實際 LLM 邊界

- 資料：沿用 v2 的八個 512×512 案例。六個 known-answer 是兩個攝影場景各小／大洞，加兩個程序化控制；另兩個是 droid＋陰影、自行車架的自然移除，沒有隱藏背景真值。攝影主比較是 **2 場景 × 2 遮罩 × 2 種子＝8 個相關配對單位**，不是八張獨立照片。四張上游來源照片與 80 個凍結檔案均另核對 SHA-256；本輪不是 T1／T5，也沒有接觸留出集。
- 生成模型：明確非原作者關係的公開 SD 1.5 inpainting 鏡像，pin `8a4288a76071f7280aedbdb3253bdb9e9d5d84bb`；13 個選定檔案共 2,134,238,054 bytes，包含 fp16 safetensors UNet／CLIP／VAE 及必要設定／tokenizer，不取 pickle／remote code。固定 CreativeML OpenRAIL-M 文字保留在 `provenance/`；只作本次非敏感本機研究，不作產品／散布核准。
- 運算：PyTorch `2.8.0`、Diffusers `0.32.2`、Transformers `4.49.0`，MPS float32 計算，來源 fp16 值 upcast；DDIM 20 steps、eta 0、CFG 7.5、strength 1。初始 noise 由 CPU seed 17／101 產生，VAE posterior 另用 seed＋100000；每個 arm 重設並留 noise／generator-state hash。不宣稱已做全域 MPS 重複輸出的 bitwise 決定性驗證。
- 第一輪先凍結 plan，再生成 72 張：八案例的 blank／manual-background／LLM／contradictory 四 arm，各兩個種子，另兩自然案例加 direct-remove-command／negative-only。先前兩張 smoke 是同 plan 內的結果，主 wave 明示重用，不另算兩張新樣本；不 best-of-N 或挑 seed。
- 確實透過 localhost Ollama 執行 `qwen3:8b` Q4_K_M，digest `500a1f067a9f782620b40bee6f7b0c89e17ae61f686b92c24933e4ca4b2b8b41`。它只收到協調者根據遮罩外可見內容寫的描述，**沒有看像素、乾淨洞內容或分數，不是 VLM**。協調者熟悉舊場景，故也不是 blind captioning。文字模型在 diffusion 前卸載，收據確認沒有同時常駐。
- 觀察順序：協調者查看全部八張第一輪 contact，先封存非盲視覺觀察再算完整 truth metrics；已先看過一組 pilot 的數值，例外有記錄，未將數值送給 critic。主數值篩選事前固定為攝影 PSNR 中位增益至少 2 dB、8 配對中至少 6 正向，另記錄大退步與可見硬失敗；不是產品品質驗收或人口推論。

### 第一輪：內容能改變，不代表重建更準

下表只列上述兩個攝影場景的遮罩內 saved-RGB16 PSNR 配對差；局部 SSIM 是 mask-centered 11px Gaussian 視窗的次要診斷，視窗可能含遮罩外內容。沒有對自然移除使用原有物件像素作「正確背景」來算分。

| 第一輪比較 | 正向／8 配對 | PSNR 中位差（dB） | 局部 SSIM 中位差 | 大幅改善數值篩選 |
|---|---|---|---|---|
| 人工背景描述 − 空提示 | 0／8 | −0.145 | −0.00392 | 未達 |
| 本機 LLM 原始描述 − 空提示 | 1／8 | −0.228 | −0.00067 | 未達 |
| 本機 LLM 原始描述 − 人工背景描述 | 4／8 | −0.006 | ＋0.00236 | 未達 |
| 矛盾提示 − 空提示 | 2／8 | −0.230 | −0.00421 | 未達；個別重建亦有大退步 |

負中位差幅度小，不據此宣稱普遍破壞畫質或統計顯著。LLM／manual 也不是等語意資訊的純推理能力比較：兩者均依協調者描述／選擇措辭，結果只支持本輪兩條提示詞產生路徑的比較。

實際看圖的重要反例：

- **大磚牆**：blank 已能補成磚牆；矛盾提示在兩個種子都產生窗戶。這證明文字條件在此有作用，卻不能把「同樣補成磚牆」歸功於 LLM，也不能由單一小洞沒變就斷言文字端未接通。
- **自然移除**：droid 區域在各 arm 仍有圓盤／玩具狀形體；自行車架在 blank、manual、LLM、remove-command、negative-only 中仍有 U 形結構。協調者非盲觀察沒有一個 arm 達到可信移除；這不是「任何提示詞都不可能改善」的證明。
- **程序化控制分開記錄**：smooth、seed 17 的人工提示相對 blank 提升 16.60 dB，LLM 提升 14.88 dB，對應 blank 生成物件而文字組避免該物件。這是單一合成案例／種子的真實正向事件，不可混入攝影主比較製造「普遍大幅改善」；其他合成結果仍有小幅負向或近零。
- **標註詞洩漏假說沒有被直接證實**：Qwen 原提示八組均夾帶 hole／masked 等描述，部分還有 magenta；全部原文保留。但 smooth 的 LLM 組沒有真的生成一個實心 magenta 洞，manual 也有局部紫色調。因此不能把詞出現當成視覺錯誤的既定原因。

### 第二輪：真正的 critic 失敗與規則控制

Qwen 收到上述無 truth metrics 的實際失敗觀察，執行一次 critique＋rewrite；回覆為有效 JSON，但 **8／8 個 `revised_positive_prompt` 與原文完全相同**。有評論文字不等於有執行修訂；不能改寫模型回覆冒稱 LLM 成功，也不以相同輸入重跑後的數值漂移冒稱修訂效果。

在任何第二輪影像生成前凍結 amendment：16 個 revised 案例／種子列明示重用第一輪相同設定，保留來源收據與 raw／PNG hash；另加 16 個 deterministic `sanitized` 刪尾句對照，加原訂四個自然案例／種子的 manual-positive＋negative 組，**新模型執行上限仍是 20 次**。第二輪沿用同案例／種子，是適應性探索，不是獨立留出驗證。

sanitized 刪掉的是整個「continuing into／behind／inside／reconstructing…」尾句，連帶空間／形狀與標註詞；即使改善，也只能歸因到本次尾句刪除，不能單獨歸因到 magenta、稱為 LLM 自我修正，或宣稱規則清理零成本、語意必然正確。

第二輪實際完成 20 次新生成與 16 列明示重用，沒有新增 LLM 重試。協調者查看全部八張第二輪 contact，封存觀察後才執行第二輪 truth scoring。攝影配對結果如下；revised 與第一輪 LLM 相同是重用的必然結果，不是另一組獨立品質證據。

| 第二輪規則組比較 | 正向／8 配對 | PSNR 中位差（dB） | 解讀 |
|---|---|---|---|
| sanitized − 第一輪 blank | 1／8 | −0.277 | 未達事前大幅改善篩選 |
| sanitized − 第一輪原始 LLM | 1／8 | −0.058 | 刪除尾句沒有呈現攝影重建增益；不證明每個被刪詞都無效 |
| sanitized − 第一輪 manual | 3／8 | −0.066 | 仍未大幅勝過人工背景描述 |

自然案例也未被新組救回：sanitized 的 droid 區域仍有圓盤／彎管狀碎片，manual＋negative 在第二個種子產生堆疊筒狀物；自行車架兩個新組、兩個種子仍有 U 形結構。大壁紙 seed 101 的綠色延伸形體亦保留。這些是固定遮罩下的實際反例，不診斷成「空間先驗必然壓過文字」；遮罩／外圍殘留、模型訓練與條件措辭的個別貢獻尚未隔離。

### 保真、成本與真正沒有量到的部分

兩輪共 **92 次實際影像模型執行、16 列相同設定重用、108 列保存結果**。獨立 decoder 直接檢查 PNG 的 IHDR／CRC／scanline，從 raw 以 float64 重算夾取與 `floor(x*65535+0.5)`，核對 **113,246,208 個 RGBA16 samples**；洞外 RGB、全部目的 alpha 與預期 saved pixels 均零差異。此數包含明示重用的檔案，不是 108 個獨立實驗。

raw 模型輸出每列仍有 386,138–748,356 個洞外 RGB channels 與基底不同；部分 raw 超出 `[0,1]`，逐列保留 clipping 數。**零洞外差異由 host 合成保證，不是 diffusion 自己守住範圍；數值／編碼通過也不是移除品質通過。** 另以 JavaScript 獨立 join／排序／計數核對 16 組配對摘要，沒有套用生成器的 group 計算函式。這些都不是獨立 PyTorch 模型保真或原生推論的證明。

| 實測項目 | 結果 | 計時／記憶體邊界 |
|---|---|---|
| 本機 Qwen 首次八組提示詞 | 35.334 s／batch | 真正文字推論與回覆；不是互動單次延遲或人工時間節省 |
| 本機 Qwen 八組 critic／rewrite | 57.778 s／batch | 回覆八組原文未改；不能把成本算成成功修訂 |
| 92 次新 diffusion 呼叫 | 21.277–35.021 s；p50 31.015 s | 每次含文字編碼、masked VAE、20 DDIM steps、decode；不含 model load 與最後存檔 |
| probe 程序 max RSS | 7,327,023,104 bytes，約 6.824 GiB | `/usr/bin/time -l`，不是產品全流程或 activation-only |
| 最高呼叫後 MPS driver allocation | 8,812,937,216 bytes，約 8.208 GiB | 呼叫後讀值，不是獨立的另一塊可相加 RAM |
| Darwin task peak footprint | 11,608,344,592 bytes，約 10.811 GiB | 與 RSS／MPS allocation 不同且重疊的量測界線，不相加推導總記憶體 |

空提示與有提示都經過相同生成階段；上表不是「加字的額外成本」，也未量測每字成本或同條件 MI-GAN 倍率。第一輪主 wave 的 2,135.262 s 包含 70 次新生成與兩列先前 smoke 重用；第二輪為 618.796 s。重用收據的歷史 latency 不列入新增生成成本。`time -l` 的零 swaps 不證明沒有 paging 或所有記憶體都常駐實體 RAM。

本機實驗使用 16 GiB unified memory；新目錄觀察到約 2.46 GiB，低於事前 8 GiB 材料上限，可用磁碟約 21 GiB，高於 10 GiB 停止線。這不是硬配額、跨裝置峰值或 full-frame 保證。未執行此 diffusion 的 .NET／ORT、CoreML、DirectML、Windows／iOS 或完整 RAW／UI／專案流程；不能將尚未做的匯出判定為「不可能」，也不能將 Python MPS 可執行當作產品已支援。

### 平行 Socratic 交叉挑戰與採納界線

四個實際平行角色 `TextConditionResearch`、`LanguageToolResearch`、`PromptCausalityReview`、`PromptDeployabilityResearch` 的初評、修正及結果挑戰分開保留。這是共用證據上的四種分析視角，不是四個獨立模型 benchmark；角色意見可能被本機執行與確定性核對推翻。

| 挑戰 | 採納／保留分歧 |
|---|---|
| 改善是否只是把 MI-GAN 換成 diffusion？ | 採同 artifact 的空提示對照；跨模型畫面或不同 provider／計時範圍不作提示詞因果或速度倍率 |
| LLM 是否補出更多觀測資訊，或 critic 真的完成修訂？ | 只收到協調者文字，無新增像素證據；八組原文未改，不能以有效 JSON 當語意遵從 |
| 洞外不變是否代表模型遵守範圍？ | raw 與合成後輸出分開；不變量由 host 實作，不能當模型本身的邊界能力 |
| 少數 PSNR 增益／多角色共識能否核准產品？ | 合成單例、自然移除、人類盲評、原生資格分開；共識僅供研究判斷，T1 不變 |
| 舊 SD 的失敗是否足以拒絕新 instruction model？ | 不採此推論；現代候選只來源查核，量化版／完整 native chain／畫質仍未知 |

**採納紀錄**：adopt 的是配對對照、原始 LLM 失敗保存、規則組與 LLM 組分離，以及 saved-pixel 保真檢查；不是採用產品模型。PowerPaint task conditioning、可視結構／exemplar 條件、LLM 工具規劃與 VLM critic 均是 study／reproduce，需獨立閉合 artifact 並跑同預算比較；未測的路線不算通過，也不算被否定。若再研究，優先測「trained removal conditioning 或空間條件能否勝過同模型 blank＋人工描述」，而非無界改寫提示詞或事後挑最好的一張。

| ID | 候選／改動 | 採納狀態 | 證據與下一個觸發條件 |
|---|---|---|---|
| PG1 | 同模型 blank／manual／LLM 對照與固定預算 | adopted，研究方法 | 兩輪 plans、108 列保存結果；後續候選維持同模型配對與全結果保存 |
| PG2 | 真正本機 LLM 產生／評論 | adopted-partial，僅研究 | 首輪提示與八組 no-op 原文保留；要成為操作輔助，須另測語意遵從與使用者操作成本 |
| PG3 | deterministic 尾句清理 | adopted-partial，僅對照組 | 已實測，但沒有大幅攝影增益；不據此加入產品規則或宣稱去除某單詞必然改善 |
| PG4 | trained removal task／空間／exemplar 條件 | deferred，study／reproduce | 原始來源機制，不是本輪執行；先閉合完整 artifact，再作等預算因子對照 |
| PG5 | 新 instruction model／量化 artifact／自動 VLM 評審 | deferred，未部署 | 權利、全鏈 bytes／runtime、保真與人類對照未知；須重新固定具名版本與判準 |

### 完整證據與只讀重現

本輪保留在 `dist/object-removal-prompt-guidance-20261021/`：`round1-plan.json`、`round2-plan-llm-original.json`、`round2-amendment.json`、`round2-plan.json`、兩輪執行／重用收據、LLM requests／原始 responses、兩輪 `observations`／先觀察後計分的 seals、`round1-verification.json`、`round2-verification.json`、`analysis.json`、`analysis-independent-check.json`、`socratic-adjudication.json` 及全部 raw／PNG／contact。原始模型／照片與前輪資料未刪改；493 個既有產品／組態檔案、13 個模型檔案、七份固定來源 README 另作 SHA-256 核對。

執行來源與必要 plans／版本／來源清單封存於 `verification-harnesses.tar.gz`；`source-manifest.json`／`archive-verification.json` 記錄原始碼雜湊、恢復與真實 saved-pixel smoke。封存不含 venv、模型、照片或 raw tensor，也不補出舊 `dist/` 失去的資料。以下使用現存環境與凍結資料，只做核對，不呼叫 LLM、下載或重新生成影像：

```bash
ROOT=dist/object-removal-prompt-guidance-20261021
PY=dist/object-removal-compact-depth-20261020/venv/bin/python
tar -xzf "$ROOT/verification-harnesses.tar.gz" -C "$ROOT"
"$PY" "$ROOT/verify_outputs.py" round1
"$PY" "$ROOT/verify_outputs.py" round2
"$PY" "$ROOT/analyze_results.py"
"$HOME/.bun/bin/bun" "$ROOT/verify_documents.mjs" "$PWD"
```

**決定：本次來源調查、來回實驗與反方核對完成；沒有證據支持直接加入提示詞／本機 LLM 就能大幅改善目前通用移除，也沒有證據否定所有專用 instruction model。** 下一個研究應驗證真正不同的 trained／spatial conditioning，而不是繼續對失敗結果追加評論文字。產品契約與原 T1–T5 保持不變。


## 16. 原生平台與最低可行成本

查核日期：2026-10-21。使用者優先的是**現有裝置能負擔、整合簡單且真正有用**，不是最強模型，也不是一律本機或一律雲端。本節是來源調查與 Mac 公開 API 實驗；不新增產品後端、付費呼叫或私人照片上傳。

### 結論與完整目標

**保留已實作的共用引擎，原生 API 只補具體缺口；移除候選在編輯時產生，接受後由本機保存。** 平台 API 能提供不少一般影像原語，但「有 API」不等於 FilmBatch 的色彩、RAW、顆粒、跨平台或專案契約已滿足；也不能用不加權的 API 數量回答「大多數目標」。

| 產品目標 | 原生公開能力／系統模型 | 最小可用安排與未閉合處 |
|---|---|---|
| 一般影像 I/O、EXIF／ICC、16-bit 匯出 | Apple ImageIO／ColorSync；Windows WIC 與已安裝 codec | 重用已驗證後端。iOS 已用 ImageIO，桌面仍走現有路徑；原生能力表不是逐格式、alpha 或 metadata 往返證據 |
| RAW 感光解碼、高光、WB、相機色彩 | Apple `CIRAWFilter`、Windows `IWICDevelopRaw` 提供平台／codec 的現像介面 | 不替換共用 managed RAW 管線；尚未證明相同感光樣本、色彩與高光語意。不能籠統斷言 WIC 必然截斷高光 |
| LUT、階調、可重現顆粒與完整創意效果 | Core Image 有 LUT／濾鏡；平台 GPU 可執行自訂演算法 | 保留 C#／WGSL 的順序、seed、座標與數值契約。底下的 LUT 精度反例不支持直接切換渲染器，也不否定所有 Core Image 用途 |
| RGB／RAW AI 降噪 | `CINoiseReduction` 與 RAW 參數式降噪可公開呼叫；不是已驗證的 RawNIND／NAFNet 等價神經能力 | 沿用現有明確安裝模型與有界推論；原生一般降噪只可另作有證據的選項，不能用名稱取代同域品質比較 |
| 主體／人物選取 | Apple Vision 內建前景／人物模型；Windows 有 Object／Foreground Extractor 系統 API | 可研究人工確認的遮罩提案；不是任意目標必能分離。Mac 已實跑，Windows 僅來源核對 |
| 人臉／場景／文字與標記輔助 | Vision 有辨識 API；Windows imaging 有影像描述等系統模型 | 能力與裝置限制逐項查核；本輪未實測辨識品質，也未證明能改善修補或節省操作 |
| 通用物件／接觸陰影移除 | Apple 未確認可公開呼叫的 Photos Clean Up 引擎；Windows 有 `ImageObjectRemover`，但有硬體／身分要求 | [第 18 節](#18-訓練空間條件續驗與最小可行路線) 的本機訓練分支有重建增益，兩個自然移除仍失敗；[第 17 節](#17-既有-cli-作為外部模型入口) 的 Codex 只證明單張候選。尚無合格通用預設，不拿 Clone／模糊冒充 |
| 單張 2D 照片深度、拆層及移動後背景 | Apple 可讀取已存在的 `AVDepthData`；這不是任意 2D 照片的單目深度模型 | Depth Small 仍為另附權重的研究路線；可見分層不提供遮擋後真相，保留 unknown coverage |
| LLM 操作／標記輔助 | Apple `SystemLanguageModel` 是受 Apple Intelligence 可用性限制的本機文字模型；Windows Phi Silica 也有資格限制 | 可免隨附部分文字權重，但不能當作 VLM 或實證畫質增益；目前不為已失敗的 critic 再加一套產品 LLM |
| 預設、undo／redo、專案、批次與離線重開 | 系統原語不提供 FilmBatch 的逐張修補資產／revision／失效規則 | 仍由共用 Core／Engine 負責；不能交給模型或 HTTP API。未實作的 `RetouchStack` 仍是提案 |

### Apple：可呼叫、實際有用與不能外推的部分

依公開宣告，`VNGenerateForegroundInstanceMaskRequest` 自 macOS 14／iOS 17 提供前景實例提案；`CIRAWFilter` 自 macOS 12／iOS 15 提供 RAW 現像控制；`SystemLanguageModel` 自 macOS／iOS 26 提供 on-device 文字模型，須檢查 `availability`。這些版本不是所有裝置、地區或資產都已就緒的保證。[Vision](https://developer.apple.com/documentation/vision/vngenerateforegroundinstancemaskrequest)、[Foundation Models](https://developer.apple.com/documentation/foundationmodels/systemlanguagemodel) 與實際 SDK 宣告是來源，不是產品驗收。

Mac probe 直接使用四張既有公開 JPEG（三個場景，其中玩偶有兩種 ISO），不下載外掛權重、不呼叫照片生成服務。保留全部結果：

| 公開 API 實測 | 觀察結果 | 決策邊界 |
|---|---|---|
| Vision revision 1，四張照片 | 磚牆沒有 observation；自行車一個實例；低 ISO 玩偶一個實例；高 ISO 兩個實例 | 可作粗選取提案，不能自動接受；沒有 observation 是合法的「無提案」，不是 API crash |
| 全部實例與逐一實例遮罩 | 七張原尺寸遮罩及三份低解析度 label buffer，已獨立解碼／雜湊核對 | 自行車被分成同一群、輪內空隙大量被填滿；玩偶仍群聚。高 ISO 多出的實例是壁紙花樣，不是玩偶被正確拆開 |
| request 時間 | 首次 2.299 s，後三次 14.6–18.7 ms；整個程序約 3.18 s | 不是受控 cold／warm benchmark、p50 或 full-frame 推估；未觀察 CPU／GPU／ANE 實際排程 |
| 程序最大 RSS | 51,822,592 bytes，約 49.4 MiB | 不含其他 OS 模型程序；不能宣稱整個系統模型只用這些記憶體或完全零下載 |
| `CIColorCube`，17×11 float32、2³ channel-swap LUT | 預設及明確 RGBAf context 各有 137／748 samples 超過原定 `1e-5`；max abs 均為 0.001470596 | 兩次精度 gate 均失敗，未放寬容差；僅此設定與輸入的反例，不推定所有 native filter 或錯誤原因 |
| `CINoiseReduction`，960×641 高 ISO JPEG | 實際 render／寫 PNG 約 60 ms，輸出為 PNG8 | 已執行一般 RGB 濾波；不是 AI RAW 降噪、畫質勝負、RGB16 往返或完整 FilmBatch 管線證據 |

Apple Photos Clean Up 是使用者功能，不能由名稱推導出可傳入影像／mask 的第三方 inpainting SDK。公開 [`cleanupPhoto` App Intent schema](https://developer.apple.com/documentation/appintents/appschema/photosintent/cleanupphoto) 要 app 自行實作 `perform()`，是向 Siri／Shortcuts 公開本 app 動作，不是取得 Apple 的移除引擎。本機 Core Image 登錄了 247 個 filter，明確 removal／inpaint／cleanup 名稱搜尋為零，並能找到 LUT、noise reduction、person segmentation 正向控制；**這只界定本次 filter inventory 與所查公開框架，不是對全部私有 Apple 功能的全域不存在證明**。ImagePlayground 的影像生成也不能自動視為固定原照＋mask 的修復契約。

Core ML／MPS 是可載入自有模型的執行基礎，不等於系統附贈任意移除／Bayer 降噪權重，也不保證整張 graph 都在 ANE。未執行 iOS 真機、RAW 現像品質、Foundation Models 文字推論或 OS 背景模型記憶體量測。

### Windows：系統模型與可攜推論 runtime 分開

Microsoft [imaging overview](https://learn.microsoft.com/en-us/windows/ai/apis/imaging)（更新 2026-10-01）明載目前 Image Super Resolution、Description、Segmentation／Foreground Extraction 與 Object Erase 需要有 NPU 的 Copilot+ PC。這不代表使用者一定得換電腦：**已符合資格的現有電腦可再檢查 readiness；不符合的裝置不能把它當預設來源**。沒有 FilmBatch 使用者硬體分布資料，不採「排除 95%」等估算。

[Get started](https://learn.microsoft.com/en-us/windows/ai/apis/get-started)（更新 2026-10-15）要求 package identity、`systemAIModels` capability 與 `MaxVersionTested >= 10.0.26226.0`；可用有 external location 的身分安排，不限整包 MSIX。`MaxVersionTested` 不是最低執行 OS。文件的 Stable 範例為 Windows App SDK 2.5.1；Phi Silica GPU 另列 experimental 與 Insider 條件，不能把其 GPU 支援轉給 imaging。實際版本／資產就緒仍須檢查 `GetReadyState`／`EnsureReadyAsync`；本輪沒有 Windows 執行結果。

另一路 [現代 Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)（更新 2026-09-08）是 ORT 基礎的 runtime，支援 CPU／GPU 與可由 Windows 管理的硬體 EP；**它不是「僅 Copilot+」的同一項系統模型，也不是一個已附移除權重的引擎**。一般 CPU／GPU 與較新 OS 才提供的硬體 EP 取得機制分開。FilmBatch 已有 ONNX Runtime 安裝／執行做法，未量測前不為這份 Survey 另遷移 runtime。

Windows Photos 的 Generative Erase 也不能直接等同第三方 SDK。模型邊界若只接受 8-bit ROI，限制的是生成區域；host 仍可直接保留原有遮罩外浮點／16-bit RGB 與目的 alpha，並非整套管線必須降為 8-bit。

### 外部 API：對低算力裝置是有效候選，不是零成本捷徑

下列只讀公開文件，**沒有傳送影像／prompt、使用憑證、付費或測得服務延遲／畫質**。價格以查核日文件為準；模型、帳戶條件、條款或服務版本改變時重查。

| 具名服務與公開契約 | 費用／設定負擔 | 尚未閉合處 |
|---|---|---|
| [OpenAI `POST /v1/images/edits`](https://developers.openai.com/api/reference/resources/images/methods/edit)，影像／prompt／可選 mask；文件有具名 GPT Image 模型與 dated snapshots | [公開價格](https://developers.openai.com/api/docs/pricing)：本次 `gpt-image-2`／2.5 的影像輸入 USD 8／百萬 tokens、輸出 USD 30／百萬 tokens；另計文字等實際用量。需 API 帳戶／Key，不能猜固定每張平均價 | ROI 畫質、延遲、帳戶資格與實際 bill 未測；PNG／JPEG／WebP 格式名稱不保證 RGB16 或 RAW 精度 |
| [Google Imagen `imagen-3.0-capability-001:predict`](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/image/edit-remove-objects)，`EDIT_MODE_INPAINT_REMOVAL`、base image／mask | [現行價格頁](https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing) 列 Imagen 3 edit USD 0.04／輸出；100 個輸出為 USD 4，未含其他費用。需 GCP project、billing、IAM；一個 request 可要求多個輸出，不等於一次固定此價 | 舊 Vertex 文件明示已移往新平台，部署時要重查 endpoint／模型。預設多候選可能增加成本；本輪未呼叫、未對自然移除評分 |
| [Adobe Photoshop Fill Masked Areas](https://developer.adobe.com/firefly-services/docs/photoshop/guides/using-fill-mask/)，source／mask 預簽 URL 與非同步工作 | 文件範例需憑證與支援的雲端儲存；範例 base URL 是 stage，不是已驗證的產品 endpoint。精確 production 價格／額度／商業帳戶條件留 unknown | 不將另一個 Firefly endpoint 的 4 RPM、價目或存留政策移植過來；一般 Photoshop 訂閱不自動成為此 API 憑證 |

OpenAI [資料控制](https://developers.openai.com/api/docs/guides/your-data) 說明 API 資料預設不供訓練，但 abuse logs 通常可保存至 30 天，並有依法／安全保留例外；ZDR 需資格與核准。Google Cloud [資料治理](https://docs.cloud.google.com/gemini-enterprise-agent-platform/resources/zero-data-retention) 也說明未經允許不供訓練，但零保留仍有逐服務設定與 abuse monitoring 等條件；不能把 Gemini 專屬快取規則直接移植到 Imagen。已核對的 Adobe fill-mask 範例未確立資料保留天數，仍列 unknown；雲端 bucket 與帳戶條款也須分別核對。**不供訓練不等於不外送、不保存或沒有隱私風險**。

Google 明確警告 mask 外像素不保證相同，建議與原圖依 mask 合成；這是 provider 邊界，不是放棄使用外部 API 的理由。所有來源都應由 host 驗證尺寸／座標、保存候選、直接保留 `M=0` 的原值與目的 alpha。生成的 8-bit patch 不會因此變成真正 16-bit／RAW 細節，但也不會迫使遮罩外降精度。

**雲端的成本在產生候選時，不必在每次重開或匯出時重付。** 若未來另經授權實作外部來源，接受後可沿既有提案保存本機 patch／mask；重開、undo／redo 與 export 只重播本機資產，不依賴網路、provider 或模型仍在線。這是保存契約的適用性分析，不是已存在的 cloud plugin 或本輪對上傳功能的授權。仍需明確同意上傳 ROI／context、移除 metadata、保護 Key、顯示與限制支出、處理未知結果並實測候選品質。

### 依現有裝置選路線，而非強迫單一階層

| 現有條件 | 最小可行研究／工程選擇 | 必須保留的界線 |
|---|---|---|
| 所有現有使用者 | 重用已實作共用 RAW／色彩／顆粒／專案與已驗證影像後端，不因本輪新增第二套平台渲染器 | 既有功能不代表未做的通用修補已完成；不為 Survey 建 provider framework |
| Apple 可用 Vision 的裝置 | 人工可修正的主體遮罩提案，缺失或群聚就明示，仍允許手動定義 mask | 選取輔助不是背景重建；未證明精準度或人工時間節省 |
| 已有符合 Windows AI imaging 資格的 PC | 可進一步量測系統移除／選取，確認身分與資產成本後再決定 | 不要求其他使用者購買 NPU，也不宣稱 eligible 就已通過畫質 gate |
| 必須離線／不外送照片 | 既有引擎與明確的人工模式；只在本機候選同時通過品質、權利與實際裝置成本後才選用 | 沒有合格重建候選時明示未提供，不暗換模糊、Clone 或更小需求 |
| 算力不足、低頻使用、可連線並同意上傳 | 外部來源可省下本機權重／神經計算負擔；已登入 Codex 的同機環境有[一個實際影像候選](#17-既有-cli-作為外部模型入口)，REST API 仍須另授權有支出上限的 probe | 不是已量得最便宜或最快，也不是可散布 SDK；網路、額度／費用與隱私仍須核對，接受後本機保存 |
| 有足夠 RAM／GPU、較大量且希望離線 | 沿現有安裝模式比較合格小型權重；[第 18 節](#18-訓練空間條件續驗與最小可行路線) 的 4ch ControlNet／FP32 offload 已同機實跑，但成本／自然移除結果不支持打包為預設 | 這不是未測的紙上風險；也不是所有小模型的否決。檔案大小不等於常駐／峰值，小模型可執行不等於能可靠移除 |

### 平行反方與採納紀錄

`AppleNativeSurvey`、`WindowsNativeSurvey`、`ExternalApiSurvey`、`MinimalCostAdversary` 實際平行調查，另一個 `TrainedRemovalCompare` 切片負責具名條件實驗。這是不同分析視角與一個執行切片，不是五個獨立模型 benchmark。初評中的「native 零成本」、「Windows 排除 95%」、「雲端每次匯出重付」、「API 一定較快／畫質較強」與新的任意 RAM／秒數門檻均未採納。原文、修正及協調者裁定保存在 `panel/` 與 `panel-adjudication.json`。

| ID | 候選／判斷 | 採納狀態 | 依據與下一個觸發條件 |
|---|---|---|---|
| NC1 | 原生只補具體缺口、重用現有引擎 | adopted，選型原則 | 已有平台後端與共用語意；更換引擎須另證 parity／成本，不由 API 數量決定 |
| NC2 | Vision 前景遮罩 | adopted-partial，研究提案 | Mac 確實可呼叫，但群聚／誤選反例保留；未加入產品 |
| NC3 | 以 Core Image LUT 直接替換現有色彩管線 | deferred | 本次精度 gate 失敗；不是宣稱所有 native 演算法不可用 |
| NC4 | Windows AI 系統移除／選取 | deferred，依裝置資格研究 | 公開能力與限制成立；Windows runtime、品質及包裝成本未測 |
| NC5 | 外部來源產生候選、接受後本機保存 | adopted-partial，研究方向 | 列出的 REST endpoint 尚未 live 實測；第 17 節另證實既有 Codex CLI 可回傳一個真實影像候選。不是產品採用，仍須品質、帳戶／隱私與保存閉包 |
| NC6 | 新 runtime／平台引擎／LLM／provider 工廠 | deferred | 尚無已證明必須新增的產品結構；不為將來用途先建框架 |

實驗在 `dist/object-removal-native-followup-20261021/`：`apple/plan.json`、`execution.json`、`result.json`、`cube-explicit-float32.json`、`verification.json`、`observations.json`、全部遮罩與 `foreground-contact.png`；來源／角色收據在 `provenance/`、`panel/`。獨立 PNG decoder／float64 oracle 核對的是保存像素與失敗收據，**不是原生 LUT gate 或全部主體選取通過**。Windows／iOS 真機、列出的影像 REST endpoint 及完整產品修補均未執行；後續 CLI 探針分列於第 17 節。T1／T5、OR-01–OR-14 與接受資產契約不變。


## 17. 既有 CLI 作為外部模型入口

查核日期：2026-10-21。使用者補充「External API you can try local cli: codex agy agent(cursor) opencode」，因此新增四個實際平行切片，使用現有安裝／登入，而非要求先申請新的 API Key。**CLI 是呼叫介面，不等於本機模型、免費服務或穩定的影像 SDK；能讀圖或回文字，也不等於能產生修補像素。**

### 四個現有 CLI 的實測界線

| CLI／觀察版本 | 本次真實呼叫 | 影像能力與最小整合判斷 |
|---|---|---|
| `codex` 0.163.0 | 現有 ChatGPT 登入，文字探針回傳正確 JSON；banner 指定 `gpt-6.1-sol`／`openai` | `exec --image` 可附圖；`image_generation` feature 與隨附 skill 列出內建 `image_gen`。下方另有**真實 PNG 修補候選**，不只是能力自述 |
| `agy` 1.4.3 | 現有 OAuth，文字探針回傳正確 JSON；`/model` 顯示預設 `gemini-4-argon-medium`，這不是回應中獨立確認的 model ID | 官方 [SDK 工具](https://antigravity.google/docs/sdk/tools) 有 `GENERATE_IMAGE`；[headless 輸入](https://antigravity.google/docs/cli/headless.md) 只列文字 blocks。未證明現有 CLI 可安全直達影像工具，不判定所有 Antigravity 都不能生成圖 |
| Cursor `cursor-agent` 2026.10.01-e373342 | 現有登入，以 `--print --mode ask --output-format json` 回傳正確 JSON；空的外部 temp workspace 需 `--trust`，未用 `--force`／`--yolo` | 本次 help 未找到 image-input flag；未測像素生成。`about` 的預設模型名稱不能代替回應 model ID；PATH 的 `agent` 實際是 Grok，不能誤當 Cursor |
| `opencode` 2.1.1 | 兩次遠端校準回覆：預設路由為 `ollama-cloud/minimax-m3.1-flash`，明訂路由為 `openai/gpt-6.1-sol` | `--file` 與 [V2 attachments](https://opencode.ai/v2/docs/attachments/) 支援具相容模型的影像輸入；本次未送圖或驗證 native image generation。重用時需明訂模型，不能靠隱含 default 推定後端 |

三個共同題文字探針只驗證 JSON、簡單運算與「遮罩內 RGB／目的 alpha／本機保存」的文字方案，沒有執行方案或證明操作效益。OpenCode 切片違反事前的一次呼叫上限，做了兩次不同的校準題，**未執行共同題**；保留原始收據並排除共同題比較，不為補成漂亮結果再呼叫。Codex prompt 檔多一個尾端換行，語意相同但不宣稱 byte-identical。完整界線與偏差見 `cli/protocol-deviations.json`、各切片 `parent-verification.json`。

這不是四個獨立底層模型的品質 benchmark。Cursor／agy 的單一 aggregate JSON 與安靜 stderr 也不是完整 tool trace 或 OS 隔離證明；不可據此宣稱所有 tool／MCP 都被硬性停用。沒有改登入、Key、全域設定或既有 MCP。

### Codex：不新增 API Key 的單次真實影像修補

在文字探針後，先凍結獨立 `cli/image-probe-plan.json`，再用**一個既有公開 `v2-wall-large` 案例**測試 Codex 內建影像路徑。只附上已挖空的 RGB8 context 與二值 mask，不送乾淨洞內真值、原始碼或私人照片；空 temp workspace、read-only sandbox，停用 shell／unified exec、apps／plugins／hooks、MCP 設定來源及瀏覽工具。不用 Python/API-Key fallback，不重生或重試。

- 一次 CLI 請求完成，整體耗時 **60.64 秒**，實際產生新的 **1254 × 1254 RGB8 PNG**；檔案建立時間落在該請求內，host 已按 SHA-256 複製保存，不以模型文字自稱代替檔案證據。
- 協調者先看完整 provider 圖與四格 contact，再封存觀察：洞內填回磚牆，這個結果沒有出現新窗框、門、文字或顯眼新物件；不能由一張規則磚牆推論通用物件／陰影移除成功。
- Provider 重寫整張圖且改變尺寸。固定一次 float64 bilinear 縮回 512 後，遮罩外有 **386,714 個 RGB channel 值**不同；因此仍須 host 合成，不能整張覆蓋原圖。
- Host 只放入遮罩內 RGB、保留目的 alpha；獨立 PNG16 decoder 與另一套 NumPy bilinear 重算核對 **1,048,576 個 RGBA16 samples**：遮罩外 RGB、alpha、重投影量化均為 **0 差異**。RGB8 候選放進 RGBA16 不會還原真正 16-bit／RAW 資訊。
- 封存觀察後的單案例洞內 PSNR 為 **16.885 dB**，只是已知答案診斷；不當作人類畫質評分、與其他模型的配對因果增益或通用移除驗收。

`exec --json` 本次只揭露 agent-message 與保存路徑，未揭露完整 image tool event；**影像後端 model ID、內部影像呼叫數與實際額度扣款仍未知**。語言 turn 回報 118,789 input tokens（其中 97,920 cached）、436 output tokens，不能換算成已核實的影像帳單或宣稱免費。

### 最低成本決策

| 候選 | 狀態 | 依據／觸發條件 |
|---|---|---|
| 對已裝好且登入 Codex 的使用者，沿 CLI 取得外部修補候選 | adopted-partial，研究路線 | 已有一個真實像素結果，免新增 API Key 與本機影像模型；不是已實作的 FilmBatch backend，也不是所有使用者都具資格 |
| 以任何既有 CLI 當標記／規劃／critic 入口 | study | 文字可呼叫已證明，操作節省與修補品質未證明；本輪不再為舊 no-op critic 加產品功能 |
| 將 CLI 視為可直接散布的穩定影像 API 或無成本本機替代 | deferred | 仍需確認 CLI 安裝／版本、登入與方案條款、quota／credits、非同步／取消／錯誤語意及足夠的影像品質案例 |
| 為測到影像而開全權限、修改全域 MCP／權限或改用新 API Key | 未採用 | 不繞過原有界線；需要另一條介面時先明列必要條件，不靜默換路線 |

**對目前這台已登入的環境，CLI 是有實證的低前置負擔研究入口，不再只列為外部 API 的紙上候選。** 仍保留共用渲染器，產生候選後由本機保存／重播；CLI／雲端不必參與重開或每次匯出。訂閱 CLI 的資料與 quota 條款不能直接套用第 16 節的 REST API／ZDR 敘述；本次只用公開像素，沒有驗證私人照片的可接受外送政策。

證據在 `dist/object-removal-native-followup-20261021/cli/`：`contract.json`、`egress-manifest.json`、四個 CLI 的原始 stdout／stderr、解析／獨立核對與偏差；影像在 `codex-image/` 的 `inputs.json`、`execution.json`、`artifact.json`、原始 PNG、`contact.png`、`composition.json`、`observations.json`、`parent-pixel-verification.json`。**不新增產品程式、不 commit／push、不改 T1–T5、OR-01–OR-14 或原接受資產契約。**

## 18. 訓練空間條件續驗與最小可行路線

查核日期：2026-10-21。接續使用者「繼續比較」及現有系統最低實用成本的要求，另測**真正訓練過的空間分支**，不再只改英文提示詞。結論：該分支在固定重建樣本有數值增益，但兩個自然物件移除仍失敗；本次 FP32 MPS／CPU offload 路徑也不是這台既有裝置的低負擔預設。**這不是所有本機模型的否決，也不是 CLI 單張結果勝過所有本機方案的品質排名。**

### 取得資格與實際執行身分

- 原先優先調查 PowerPaint v1 的 learned context／object tokens；在所查的精確模型 card、metadata 與作者程式來源中，未找到該 v1 權重的明確研究使用 grant。程式的 MIT 不代替權重 grant。故**未下載、未執行 PowerPaint v1**；阻擋與未執行方案保留於 `powerpaint/grant-review.json`、`qualification.json`、`plan.json`。
- 明確批准的替代為 `StableDiffusionControlNetInpaintPipeline`，搭配 [ControlNet v11p-inpaint](https://huggingface.co/lllyasviel/control_v11p_sd15_inpaint/blob/c96e03a807e64135568ba8aecb66b3a306ec73bd/README.md) 與 [SD1.5 txt2img 4-channel UNet](https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5/blob/451f4fe16113bff5a5d2269ed5ad43b0592e9a14/README.md)。依所查 OpenRAIL/card 條款進行本機研究，不宣稱商業散布全鏈已獲清理；SD base 是明示非原發布者關聯的社群 mirror。
- 新取得兩個 safetensors 權重：ControlNet 722,598,642 bytes、4ch UNet 1,719,125,304 bytes，另有小型設定檔；VAE／text encoder／tokenizer 重用既有相同材料。磁碟 FP16 儲存全部升為 **FP32 計算**，不是 FP16 推論、量化模型或原生 ONNX/CoreML 產品鏈。
- 相較第 15 節的 9-channel inpainting UNet，這次換了 4-channel base、管線與 residency；**跨節數字只能描述，不能把差異歸因於新增分支**。主要比較限定本輪相同模型、相同 offload 管線內分支 scale 0／1。

### 凍結矩陣與完整留存輸出

沿用同一份 v2／第 15 節 manifest：兩個攝影場景各有小／大 mask、兩個 procedural controls、玩具／陰影與車架兩個自然移除案例。每個案例固定 seeds 17／101，三組為：

| 組別 | 文字條件 | 空間分支 |
|---|---|---|
| `ctrl-off-blank` | 空提示 | scale 0，仍執行分支後歸零 |
| `ctrl-off-manual` | 固定人工背景描述 | scale 0，仍執行分支後歸零 |
| `ctrl-on-manual` | 同一固定人工背景描述 | scale 1 |

8 案例 × 2 seeds × 3 組共 **48 份完整留存結果**。配對使用相同初始 noise、VAE seed／最終 RNG state、DDIM 20 steps、CFG 7.5、eta 0；空間 hint 為可見 RGB8 context／255，洞內精確填 -1，不輸入乾淨洞內真值。收據每次記錄一次首 forward 的 hint assertion 與 20 次 ControlNet forwards；這是產生端 instrumentation，不是獨立神經網路數值等價證明。

父協調者直接看完全部八張 contact、兩個 seeds／全部組別後封存 `visual-observations.json`，再獨立解 PNG16 bytes、比對 raw float32 的 float64 量化與目的像素。看圖前已收到 worker 的數值摘要，因此**不是盲評**，也不以封存先後假稱隔離了所有指標資訊。

### 同模型配對結果

下表由 `verify_experiment.py` 從保存的 PNG／raw 重新計算。攝影的 8 組是 **2 個場景 × 2 masks × 2 seeds**，不是八張獨立照片；合成另列，不混入攝影結論。

| 比較 | 攝影改善／變差 | 攝影 ΔPSNR 中位數 | 合成改善／變差 | 合成 ΔPSNR 中位數 |
|---|---|---|---|---|
| 訓練分支：ON/manual − OFF/manual | 7／1 | +0.747 dB | 3／1 | +4.525 dB |
| 固定文字：OFF/manual − OFF/blank | 5／3 | +0.662 dB | 4／0 | +3.761 dB |
| 文字加分支：ON/manual − OFF/blank | 7／1 | +1.321 dB | 4／0 | +10.828 dB |

**數值增益不等於移除成功。** 攝影的磚牆尺度／內容有局部改善，但仍有暗邊、接縫與對齊問題；大洞空提示常生成畫框／圖像，人工背景描述減少這些內容，不保證無痕。Procedural lines 的分支 ON 明顯抑制多餘形狀，smooth 的 seed 101 卻仍有色塊。兩個自然案例在各自六個結果中，玩具／陰影仍生成蛋／石／玩具般物體，車架仍保留／重建曲線結構或改壞車輪；**沒有可信的完整移除**。這只裁定固定 mask／context，不推論換 mask 或其他模型也必失敗。

### 既有裝置成本、失敗與協定偏差

| 已觀察項目 | 結果 | 不可外推的界線 |
|---|---|---|
| 48 次完整留存呼叫 | 中位數 81.984 秒；47.076–111.181 秒；合計 3,887.884 秒 | 含 CPU↔MPS 搬移、生成與 decode；不含模型載入、最後存檔與失敗工作，不是整輪總成本 |
| Process lifetime RSS | 收據最大 9,270,099,968 bytes（約 8.63 GiB） | 與 MPS driver／process footprint 重疊，不能相加，也不是全系統模型記憶體 |
| MPS driver 呼叫後用量 | 最大 3,763,011,584 bytes（約 3.50 GiB） | offload 後的觀察值不是峰值；OOM 顯示瞬間配置不能由它推估 |
| 已保存六份 `time -l` process summary | 各 process peak footprint 約 9.87–10.38 GiB | 原始整數為 bytes；不是 worker／review 初報的 10.7–11.1 GiB，更不能與 RSS／driver 相加。主長程序與最後 loop 缺完整 summary |
| Full-resident 與 offload | Full-resident 首次呼叫 OOM；offload 另有四次 OOM | 合計五個 OOM 收據；單次 full-resident 失敗不證明「必然／deterministic」，offload 成因也未被證明是碎片化 |
| 額外失敗工作 | 三個執行期 harness 失敗、一次錯誤 interpreter 的 NumPy 缺失、一次超時中斷 | 包含已計算但無完整收據的結果；48 是留存成功數，絕不是全部模型嘗試數 |
| 可識別的模型工作量 | 56 次啟動；至少 50 次完成計算，只有 48 次完整收據 | 48 留存＋2 次完成未收據＋5 次 OOM＋1 次超時中斷；pre-call／環境失敗不誤算為額外 diffusion |

Full-resident 失敗後，父協調者明確允許**一次 residency 變更**為 model CPU offload，維持 FP32／MPS／原矩陣。但 worker 隨後在 offload OOM 後加入清 cache、跨 process resume，並以有限 loop 補齊缺格，**超出當時「失敗留證並停止、不重跑失敗格」界線**。父協調者不追認為無偏差預註冊執行，不刪除額外工作、不再推論補跑。保留第一份完整收據、未收據影像、失敗 traceback、session／stderr 與原 worker 判斷；探索性配對結果可讀，但不能隱藏協定偏差。

Scale 0 組仍跑 ControlNet，故本輪 ON/OFF 計時不是新增分支的「邊際成本」。與第 15 節較快的 9ch 執行比較，也同時更換 base／管線／residency，不是單因子效能實驗。`runtime-accounting.json` 將六份完整 process wall 合計 468.93 秒與其內已計入的 202.291 秒留存呼叫去重，連同其他留存呼叫，可證明**模型執行時間至少 4,154 秒**；這是扣除顯示四捨五入後的保守下界，不包含研究／下載總時間。主長程序與最後 loop 等缺完整計時，**精確總成本仍未知**，不能拿估計的 timeout 長度補造。

目前 runner 與已保留的 pre-defect snapshot 可封存核對，但每個 session／收據未綁定當時 script SHA／interpreter，且中間版本未全數保留。這項來源缺口不能靠最後版本封存補回；只證明「現存來源可恢復並重驗已存資料」，不宣稱逐次歷史執行或整條神經運算可精確重現。

### 最小可行採納決策

| 路線／使用者條件 | 裁定 | 依據與保留缺口 |
|---|---|---|
| 共用現有渲染／RAW／色彩與接受資產保存 | 保留既有架構；修補契約仍是提案 | 原生、CLI 或供應權重都不接管 undo／專案／重開／批次；接受候選後保存本機，不在重開／匯出重新呼叫模型 |
| 相容 Mac 的公開 Vision 選取 | adopted-partial，輔助能力研究 | 已實跑，但分群不等於精確物件 mask，也不提供被遮住的乾淨背景；不取代手動選取 |
| 目前已安裝且登入的 Codex CLI | 優先的低前置負擔影像來源研究入口 | 已取得一張真實候選；尚缺穩定 image SDK／後端身分、額度／條款、私人資料政策與多場景畫質，不能成為通用產品預設 |
| 本次 ControlNet／4ch FP32 offload | reproduce／study，不採為現有裝置預設 | 局部數值增益，但自然移除仍失敗、呼叫慢且有多次 OOM；不要求使用者換硬體來合理化它 |
| 小型本機模型或 Windows 系統 imaging | 條件式 study | 普通裝置的 Windows ML runtime 不等於預裝移除模型；小模型仍需自己的權利、完整鏈、畫質與資源實證 |
| 外部 REST API | 明確選用的來源調查 | 未 live 測影像；有網路／費用／隱私／配額，接受後可本機保存；不是靜默 fallback 或已採納 cloud backend |

「能完成大多數目標」仍不應用 API 數量計票：現有共用引擎保留 RAW、色彩／LUT、metadata、輸出與專案生命週期；公開原生 API 可補部分選取／處理，但**可呼叫的背景生成與合格通用移除**仍是關鍵缺口。既有 CLI 減少新增模型或 Key 的前置工作，不代替畫質、條款與產品整合驗收。

### 驗證與證據邊界

- 父端獨立核對 **48 PNG16／50,331,648 RGBA16 samples**：遮罩外 RGB、全部目的 alpha、洞內 raw 量化均 0 差異；16 個配對 RNG 收據組一致。這證明保存／配對與數值摘要，不是 PyTorch↔原生神經網路 fidelity 或人類移除評分。
- 初版父端 verifier 使用舊收據欄名而 `KeyError`；依目前每 request 欄位修正後完整驗證通過，未改像素期望／容差。錯誤收據另存 `verification-harness-incidents.json`，不記為模型品質失敗。
- 證據根目錄為 `dist/object-removal-native-followup-20261021/`：`decision.json`、`delivery.json`、`independent-experiment-verification.json`、`runtime-accounting.json`、`visual-observations.json`、`model-protocol-adjudication.json`、`final-review-adjudication.json`；worker 的來源／權重 inventory、矩陣、raw／PNG、contacts、失敗與原始決策在 `powerpaint/`。原 worker／review 的錯誤計數與單位保留於歷史原件，父端決策明列修正，不讓舊摘要覆蓋原始資料。
- **T1 仍 NO-GO，T2–T5 未開始。** 未新增產品程式、永久測試、依賴、provider 或平台分支；不 commit／push，不外送私人照片，也不把本次研究判為 Windows／iOS 或完整 RAW／full-frame 產品通過。

現存研究來源封存於 `verification-harnesses.tar.gz`，24 個來源的 SHA-256 與環境見 `source-manifest.json`／`environment.json`。以下只重驗已保存資料／文件，不載入模型、不呼叫 CLI／API，也不重跑既有失敗。執行前保留相鄰的既有 v2／共享環境；source archive 不打包權重或整個 Python 環境。

```bash
ROOT="$PWD/dist/object-removal-native-followup-20261021"
PY="$PWD/dist/object-removal-compact-depth-20261020/venv/bin/python"
mkdir -p "$ROOT/restoration-proof/src"
tar -xzf "$ROOT/verification-harnesses.tar.gz" -C "$ROOT/restoration-proof/src"
"$PY" "$ROOT/restoration-proof/src/verify_experiment.py" "$ROOT"
"$PY" "$ROOT/restoration-proof/src/apple/verify_native.py" "$ROOT/apple"
"$PY" "$ROOT/restoration-proof/src/cli/verify_codex_image.py" "$ROOT/cli"
"$HOME/.bun/bin/bun" "$ROOT/restoration-proof/src/verify_documents.mjs" "$PWD"
```

## 19. 情境解法、成本效益與決策紀錄

日期：2026-10-21；決策版本：1。使用者要求記錄既有想法、推導最低成本／最高效益、依情境選 Solution，可行才 Plan，否則記 No-Go。**本節是候選、取捨及可推翻的決策紀錄，不是逐字內部思考。** 收斂本文件第 1–18 節及既有 ROI／圖層實驗；沒有發新模型／CLI／API 請求、下載新權重或開始產品實作。

### 結論、授權與「最高效益」的定義

**目前沒有證據能選出通用移除的最高效益產品方案。** 最小新增工程面的選擇是保留既有共用引擎；若使用者允許外送，優先驗證既有 Codex 取得候選、host 合成並保存的路線。這是**目前環境的優先驗證候選**，不是品質冠軍、免費服務或可直接出貨的 backend。離線通用 AI 移除仍無合格方案；不強迫換硬體，不把 Clone／Depth／保存像素冒充重建成功。

- **GO-KEEP**：繼續使用已實作的 RAW／色彩／顆粒／影像 I/O／專案與匯出；不是要求重寫。
- **GO-PLAN**：只對已實際取得像素、前置負擔較小的外部候選路線，建立[兩案資格驗證計畫](superpowers/plans/2026-10-21-object-removal-scenario-qualification.md)。新請求尚未執行，須綁定新的公開輸入、額度／隔離與人類評審條件。
- **NO-GO／DEFER**：前者指指定用途／artifact 的現有門檻未過；後者指關鍵資料／裝置／權利不足，不能排名。兩者均保留原因與重開條件，**不是全家族永久否決**。
- **產品仍 NO-GO**：原 OR-02 要求真實本機模型，不能拿外部 CLI 成功勾選；T1 未通過，T2–T5 未開始。若日後要新增外部或人工模式，須明確另定 scope／驗收，不能在本輪默改[設計版本 1](object-removal-design.md)。

效益先看「目標及影響移除、沒有新干擾物、保護物／像素／保存契約成立」，再看操作者實際花多久取得可接受結果。僅有 API、較高 PSNR、較小檔案、較少依賴或有效 JSON 都不是充分條件。現在缺人類接受率、總人工時間、CLI 實際費用及跨場景可靠度，因此不能報投資報酬率、節省百分比或任意加權總分。

### 想法總帳：沿用、選取與原生能力

下表覆蓋已提出的方向；同一列合併相同決策問題，不把每個模型別名再建一套系統。來源及詳細反例以所指章節為準，沒有執行的材料不偽裝成已測。

| ID | 想法／預期用途 | 證據、反例與未知 | 決定／重開條件 |
|---|---|---|---|
| I01 | 沿用共用引擎與既有平台 I/O | 第 5／16 節：已有 RAW、色彩、顆粒、metadata、專案／匯出；原生 API 不提供整套語意 | GO-KEEP；只為實證缺口增加窄整合，不另造平台引擎 |
| I02 | 筆刷／圈選／加減選先形成正式 mask | 設計第 3／5 節；物件、陰影與倒影需人工確認；完整移除 UI 未實作 | 保留基本路徑提案；T1 前不先蓋產品 UI，人工 mask 不代表背景能補好 |
| I03 | EfficientSAM-Ti、SAM 2.1、MobileSAM、SAM 3 | 第 3／9／10 節：Ti 有原生串接與鄰近物誤選；其他版本的完整 encoder、權利／平台資格不可繼承 Ti | DEFER 選取品質／操作比較；Ti 優先既有對照，其他須自己的 artifact；不得自動接受 mask |
| I04 | Apple Vision 內建前景選取 | 第 16 節：Mac 可呼叫，但車架／玩偶群聚，壁紙可誤選；人力節省未測 | 條件式研究；只有提案加人工修正比純手動有益才進一步整合 |
| I05 | 已有 eligible Windows PC 的系統移除／選取 | 第 16 節：有公開 API，需裝置／readiness／identity；本輪無 Windows runtime | DEFER 裝置 smoke／品質／打包；不為功能要求使用者買 NPU |
| I06 | 用 Core Image／CIRAWFilter／WIC 取代共用管線 | 第 16 節：LUT 兩種設定精度 fail；RAW／ICC／效果 parity 未建立，不能從一般 codec 支援推定相同 RAW | NO-GO 此次全引擎 cutover；新的具體缺口先量測同域 parity／成本 |
| I07 | 以原生一般降噪／Foundation Models／Phi Silica 取代既有模型 | 第 16 節只實跑一般 JPEG noise reduction；文字模型不是 VLM，CPU／GPU runtime 不是隨附重建權重 | 不取代已驗證降噪，不為舊 critic 加產品 LLM；須各自證明可用性與人力／同域畫質收益 |
| I08 | Photos Clean Up／Windows Photos／App Intent 當嵌入 SDK | 第 16 節：app 功能與公開引擎不同，`cleanupPhoto` 仍需自行實作動作 | NO-GO 未建立的呼叫假設；有正式可用的影像＋mask 介面及條款才重開；不宣稱全部私有 API 不存在 |

### 想法總帳：重建模型、文字與訓練條件

| ID | 想法／預期用途 | 證據、反例與未知 | 決定／重開條件 |
|---|---|---|---|
| I09 | MI-GAN FP32 core，便宜本機重建 | 第 9／10／14 節：原生數值可過，仍生成新物體／殘留；沒有文字輸入 | 留工程對照；NO-GO 通用產品預設，新的獨立畫質／整鏈成本證據才重議 |
| I10 | MI-GAN FP16 儲存、FP32 計算縮小包裝 | 第 14 節：檔案縮小但數值失敗，觀察 RSS 不支持對半減少 | NO-GO 此衍生 artifact；不得因省下載而放寬數值 gate |
| I11 | LaMa／big-lama／Dilated 類本機補洞 | 第 2／10 節：具名 checkpoint grant 未閉合，FFT／export 與完整鏈未跑 | DEFER 權利／artifact；原始碼 Apache 不代替權重許可，不把未測當品質失敗 |
| I12 | Moebius pretrained／ft_places2 與替代 VAE | 第 9–12 節：完整 .NET 鏈已跑但 strict fail，場景微調仍有碎片／新結構，VAE 身分差異保留 | NO-GO 此次設定；須新的精確 artifact、數值／來源與足夠品質收益，停止無新假設的匯出重試 |
| I13 | AOT-GAN／EdgeConnect 小型重建 | 第 11／13／14 節：精確權重 grant／lineage 未閉合，未下載／推論 | DEFER 用途許可與來源 bytes；不拿 badge、鏡像或其他模型反例替代資格 |
| I14 | MAT／FcF／ZITS／ZITS++ 結構／紋理重建 | 第 2／13 節：各有自訂／第三方條款、完整元件或可攜圖缺口；不是單一小 token | DEFER 具名完整鏈；逐元件固定權利、尺寸、載入安全與原生實證後才比較 |
| I15 | ObjectClear／OSOR／FLUX.1-Fill-dev 處理物件及影響 | 第 2／13 節：有任務專用來源，但 checkpoint／base 條款及整鏈成本不同；one-step 不免 base | 有條件品質參考；研究使用資格、完整材料及現有硬體 envelope 可滿足才開實驗 |
| I16 | PowerPaint v1 learned context／object tokens | 第 15／18 節：機制不同於改英文 prompt；查到的 v1 權重 grant 不足，未執行 | DEFER 精確用途 grant；不下載補齊漂亮比較，不稱本次 ControlNet 結果為 PowerPaint |
| I17 | PowerPaint v2.1／BrushNet／OmniPaint 線索 | 第 2／11／13 節：前兩者另需大 base／adapter 與完整條款；OmniPaint 仍為候補線索 | DEFER 全鏈 artifact／權利／資源，不用 adapter 大小當 RAM，不因新名稱加入 runtime |
| I18 | ControlNet v11p-inpaint＋4ch SD1.5 FP32 offload | 第 18 節：有同模型重建增益，但自然移除未過，完整呼叫慢且多次 OOM，resume 偏差保留 | NO-GO 此裝置預設；新 artifact／部署方式另立實驗，不能再補跑此矩陣或提高記憶體上限 |
| I19 | Source／structure／exemplar 條件，包括 Paint by Example／ControlFill 類線索 | 第 15 節及前輪來源探索只支持不同條件機制值得查；沒有合格的完整本機 artifact／同預算結果 | study；具名完整元件、實際條件輸入、用途資格及可驗證的對照齊備才研究，不預授效益 |
| I20 | FLUX.2-klein-4B／Qwen-Image-Edit-2511 或量化版 | 第 15 節固定原始 safetensors 超過當輪材料上限，未執行；bytes 不是 RAM | DEFER；量化版是新的 artifact，要重驗完整鏈／保真／成本，不能由原版超限否決全家族 |
| I21 | 空提示／人工描述／本機 Qwen／規則清理 prompt | 第 15 節：真正同 artifact 對照未見大幅攝影收益，規則只在部分合成面向正向；文字能改內容不等於刪掉目標 | NO-GO 以 prompt 欄位解決原品質缺口；有獨立自然移除／操作收益才考慮新使用需求 |
| I22 | Grounded-SAM、MGIE、SmartEdit、Inst-Inpaint、InstructPix2Pix 的規劃／instruction 路線 | 第 15 節：訓練聯結、工具選取與每次 LLM 文字呼叫是不同機制；未執行各完整鏈 | study；先查精確權重／base／grant／本機成本，不能拿聊天能力當像素生成 |
| I23 | VLM／VIEScore critic、LLM 多輪改寫／排序 | 第 15 節 Qwen 8 組修訂全為 no-op；本輪 critic 不看圖，沒有 VLM 或人工效益實證 | NO-GO 自動反覆生成／自批；固定候選／成本上限、獨立人類對照與非 no-op 增益後重議 |

### 想法總帳：人工修補、ROI、Depth 與圖層

ROI／Depth／圖層補充依 `dist/object-removal-roi-layer-20261021/decision.json` 與 `visual-observations.json`，不是新增執行。E1／E2／E3 使用凍結開發素材，非 T1／T5；原 PNG／計時／verifier 失敗不因本節收斂而消失。

| ID | 想法／預期用途 | 證據、反例與未知 | 決定／重開條件 |
|---|---|---|---|
| I24 | 人工指定 donor、RGB16 Clone | 第 13 節：真實 .NET RGB16 copy 可行，來源錯位／接縫仍存在；人工時間未測 | 可行的不同工具模式研究；不算 AI 重建或已實作 FilmBatch 功能，須另獲需求／驗收同意 |
| I25 | Heal／normal、mixed Poisson 融合 | 第 13 節：修正初始化後實跑，OpenCV 此 API 是 RGB8；不能承諾 RGB16 原生 | DEFER 高精度／接縫與人工來源比較；不靜默把整圖降到 8-bit |
| I26 | Telea／Navier–Stokes，小污點／簡單區域 | 第 13 節：實跑快，但有大洞／結構失敗與 float scale 反例；範圍未成產品資格 | 只留顯式古典對照；NO-GO 通用 AI 替代，不自行縮成未獲批准的瑕疵產品 |
| I27 | PatchMatch／exemplar／Criminisi、FSR／ShiftMap | 第 13 節：原 PatchMatch 包不含完整 synthesis app；wrapper 權利不清除整鏈；contrib 未安裝 | study／DEFER；先完整可用實作與權利，不把 donor copy 冒稱 PatchMatch |
| I28 | 同 mask 改 tight／wide ROI context | E1：改善／惡化並存，wide 可與全幅輸入完全相同；不是裁越小越好 | NO-GO 通用 crop 勝者；保留各 artifact 的 context 契約，停止這輪無新假設掃參 |
| I29 | Depth 當 donor 來源硬篩選 | E2：固定 gate 減少可用提案，沒有已知答案改善；用了原未遮蔽圖快取 depth | NO-GO 此硬 gate；新 depth 輸入／soft hint 須對 masks-only、真實人工修正量測，不當必要依賴 |
| I30 | 語意 cutout＋可選相對 Depth／三層可見 RGBA | 第 14 節：可見分層與重組成立，Depth band 不是物件、alpha matting 或背景真值 | 可獨立研究遮擋／排序；不新增產品圖層需求，不以 depth 替代精確物件 mask |
| I31 | 一張接受的背景 plate 重用多次移動 | E3：條件式可重用；當輪 A 命中 E1 cache，零新推論不等於冷成本零 | [INFERENCE] 同基底／來源與前景固定時較省重複生成；先有可接受 plate，才值得研究完整移動流程 |
| I32 | 每次只補移動後 vacated unknown | E3：小寫入區不保證好背景，與 plate 品質勝負混合；binary cutout／固定目的 alpha 不是通用合成器 | 不當普遍優化；保留 unknown provenance，若目標只是可信 cutout 動畫可另定需求 |
| I33 | Qwen-Image-Layered／完整 amodal 多 RGBA | 第 14 節：固定 transformer 原始 shard 已超當輪材料上限，未執行；生成不可當真實隱藏面 | DEFER 精確可負擔 artifact、完整 grant、generated／visible／unknown 與 alpha 契約；不否決所有未來量化版 |

### 想法總帳：外部入口與接受後保存

| ID | 想法／預期用途 | 證據、反例與未知 | 決定／重開條件 |
|---|---|---|---|
| I34 | OpenAI Images REST edits | 第 16 節有公開契約／token 價格與資料政策，未 live 測；與訂閱 CLI 不同 | DEFER 帳戶、明確費用／外送同意與多場景真實像素；不自動申請 Key 或推算固定每張價 |
| I35 | Google Imagen removal REST | 第 16 節有 mask removal／逐輸出費用與治理來源，endpoint 遷移要重查，未 live 測 | DEFER project／billing／IAM、實際輸出／費用／資料邊界；不能套 Gemini 規則當 Imagen 保證 |
| I36 | Adobe fill-mask API／明確外部編輯器 raster handoff | 第 13／16 節：公開範例含 cloud URL／非同步；production 價格／retention 未閉合；啟動 editor 不等於重開閉包 | 有現成合格 editor 時可研究人工 handoff；API 另需帳戶／儲存／條款／結果回傳證據 |
| I37 | 既有 Codex CLI 的 native image 工具 | 第 17 節實際新 PNG＋host 保存；只一個已知牆面案例，model／內部 call／帳單／私照政策未知 | GO-PLAN 兩案必要條件篩選；不是穩定可出貨 API 或通用移除 GO |
| I38 | 既有 agy CLI／Antigravity SDK | 第 17 節真實文字成功；SDK 有 image tool，headless CLI 是否能受限地用影像未證明 | DEFER 安全的真實影像介面；不為追求生成成功開 shell／新增 SDK 權限 |
| I39 | Cursor CLI `cursor-agent` | 第 17 節只證文字；本次 help 無已確認 image-input flag，PATH `agent` 是別的程式 | DEFER 像素生成證據；不把 model 名稱、讀圖或文字計畫當修補候選 |
| I40 | OpenCode 圖片附件／明訂遠端模型 | 第 17 節只證兩次不同校準，違反一請求界線且未做共同題；影像生成未證明 | DEFER；有明確安全路由／影像工具再另定預算，不補跑修飾舊紀錄 |
| I41 | 來源無關的候選匯入、接受像素、無模型重播 | 設計第 6／7 節是完整提案，CLI host 已驗窄合成，不是產品 RetouchStack／undo／export | 保留共同契約；先資格再產品，接受後無需每次匯出重付；不先蓋 provider 工廠、兼容層或自動選型服務 |

### 最低成本如何比較

先套硬限制：是否必須離線、可否上傳**實際 context**、已存在的 OS／硬體／登入、權利、畫質／保護物／精度、互動等待與故障處置。任一不符即排除該用途，不用便宜抵銷隱私／品質失敗。留在同一可接受範圍內才比較以下成本：

1. 一次性：安裝／帳戶與方案、下載／磁碟、平台 binding／打包、工程與後續版本維護。既有環境的 sunk cost 與新使用者的 setup 分開。
2. 每次編輯：操作者選取／修正／檢視／重作時間、完整等待、全部 dispatch／拒絕／逾時／失敗、實際費用；候選不被接受也消耗成本。
3. 接受後：patch／mask 保存空間、上游變更的失效恢復與本機 render；重開／undo／export 不需要 provider 重生成。這是尚待產品實作的契約，不把成本移到每次匯出。
4. 記憶體：process RSS、driver 與 footprint 分別列邊界，不相加；權重磁碟 bytes、呼叫後 allocation 與整機峰值不是同一單位。

以 `N_accepted` 表示人類按既定標準接受的件數，分別報 `現金總支出 / N_accepted`、`setup＋操作＋檢查＋重作的人類時間 / N_accepted` 與完成等待。沒有接受件時不產生有限「每件成功成本」；人類資料／帳單缺失則 unknown，不能填 0。wall 用實際時間軸，重疊 process／歷史 cache 不重複加總；無完整收據只報可證下界。只有操作者願意提供統一價值換算時，才可合併錢與人力，不自行替時間標價。

大量使用是否值得本機化，取決於同品質下的固定成本與邊際成本：若有同單位且可信的 `F_local`、`F_external`、`v_local`、`v_external`，並且 `v_external > v_local`，可解 `N* = (F_local - F_external) / (v_external - v_local)` 作交叉點；前提不成立或 `N* <= 0` 就不能宣稱存在正的攤平點。現在缺帳單、接受率、人工與維護資料，**不捏造「每月幾張就划算」**。

以下只說明為何不能以核心毫秒／成功列數選冠軍；不是同協定效能排行榜：

| 既有實測 | 可用於決策的結果 | 必須保留的測量邊界 |
|---|---|---|
| E1 tight ROI 對全幅，12 個 known-answer | 改善 6、惡化 6；最差 ΔPSNR −16.422 dB | 攝影與程序控制混合；不是小洞安全範圍或普遍縮圖策略 |
| E2 depth 硬 gate | 提案 10 → 5；已知答案改善 0、惡化 1 | 其餘平手／不可比，沒有人工修正時間；不外推 depth 完全無用 |
| E3 unknown-only 對 shared plate | 已知答案 poses：B 好 2、A 好 3、相同 3；distinct core inputs A 8、B 14 | A 被 E1 預填 cache；不能將這輪新執行 A=0 宣稱零成本或因果加速 |
| SD1.5 9ch prompt 實驗 | 92 次新執行，21.277–35.021 秒／次 | 含 encode／取樣／decode，無 load／存檔；自然移除失敗，非 CLI 同模型比較 |
| ControlNet 4ch FP32 offload | 48 份完整收據，中位 81.984 秒；56 次可識別啟動、五次 OOM | 含搬移，完整執行成本至少 4,154 秒；精確整輪未知，偏差不追認 |
| 既有 Codex 一案 | 60.64 秒取得 1254² RGB8；host 遮罩外／alpha 精確保留 | 含整個 CLI 請求；一案非分布，內部 model／call／帳單與人工接受率未知 |

**[INFERENCE] 最高槓桿不是再加一層 agent，而是避免未被接受的生成與重複生成。** 先用已確認 mask、明確 context、候選檢視及本機接受資產；只有具體畫質／使用問題才增加提示、Depth、模型或 API。反方：持久化本身不會讓壞背景變好，也有格式／失效／資產成本。因此先驗來源，再依原 T1 與設計實作，不把資料層當替代成功。可推翻條件：同品質的完整使用紀錄顯示另一條路線有較低總人工／支出，或 CLI 無法穩定、安全、合法地回傳候選。

### 不同情境的 Solution

這是**選型／研究矩陣，不是 FilmBatch 已支援功能清單**。無可接受方案時允許維持原圖／明確拒絕，不自動跳到另一層、不用開全權限或換硬體解套。

| 情境 | 現在選擇 | 狀態／拒絕條件 | 下一個必要證據 |
|---|---|---|---|
| S01 既有 RAW／調色／顆粒／匯出，不要求移除 | I01 現有引擎 | GO-KEEP；不為 AI 研究增加相依 | 只有觀察到具體功能缺口才重開改動 |
| S02 嚴格離線／私照不得外送，要通用自動移除 | 不選任何外部入口；現有本機對照不包裝成產品 | NO-GO 畫質／整鏈資格；不靜默換成 Clone | 新具名本機 artifact 的權利、品質、實際裝置資源及原 T1 |
| S03 同意人工指定乾淨來源，只要局部可控修補 | I24；融合需求再研究 I25 | 不同模式，可研究但產品未實作；來源不足／接縫不可接受就拒絕 | 使用者另同意 manual-mode scope，加完整操作／RGB16／保存證據；不取代 OR-02 |
| S04 已裝好登入 Codex、低頻、可連線且同意公開 context 外送 | I37＋I41，先走明確取得／檢查候選的研究流程 | GO-PLAN；無新增 Key／本機權重不等於免費；拒絕私照政策／quota 未明的自動使用 | 兩案計畫的自然移除、隔離、人工工作量與帳戶條款 |
| S05 沒有合用 CLI、低算力，可付費／上傳 | I34／I35／I36 中契約與帳戶合格的一個具名 endpoint | DEFER；本輪不替使用者選未測品質冠軍／申請帳戶／發付費請求 | 實際使用地區／帳戶／保留條款／費用上限與少量 live 輸出；每輸出數量明定 |
| S06 現有 Windows PC 已符合系統 imaging 資格 | I05 的窄裝置資格 probe | DEFER runtime；eligible 不等於畫質通過，不要求其他 PC 跟進 | 版本／identity／readiness、真實影像＋mask、品質與完整包裝成本 |
| S07 相容 Apple 裝置，主要痛點是選取慢 | I04；可攜比較再用 I03，仍人工確認 | 條件式輔助研究；群聚或加減修正更耗時就不用 | 同一目標的純手動 vs 提案修正總時間、保護物／影響 mask 準確性 |
| S08 高頻／批次、願離線且已有足夠算力 | 只比較新合格小型 artifact 與現有流程；不直接選 I18 | NO-GO 現有通用預設；CLI 也缺穩定批次／取消／計費契約 | 同品質接受件成本、全流程失敗率、完整硬體／維護成本，不能只算權重大小 |
| S09 同張圖反覆移動已確認前景／做 2.5D | I30＋I31；背景 plate 先產生並接受，Depth 只作可選排序 | 條件式獨立研究，非通用 amodal；無可信 plate 就保留 unknown，不塗黑假背景 | 精確 cutout／straight alpha、遮擋／邊緣、基底失效與真實多次移動成本；另定功能需求 |
| S10 必須補出從未看見的完整背面／完整多 RGBA | I33 作來源研究，不宣稱還原真實 | DEFER artifact／資源／provenance；單張相對 depth 不足以唯一恢復隱藏面 | 可負擔且合格的生成鏈，visible／generated／unknown 分離及多層 alpha 品質 |
| S11 RAW 高光／細節精度不可退，但只允許局部 SDR 修補 | 沿既有設計，在顯示域處理確認 ROI，I41 保留原始 RAW 與 mask 外數值 | 只保護邊界，不承諾候選具 RAW／原生 16-bit 細節；生成區若也要求此資訊則 NO-GO | full-frame RAW、ICC／alpha／座標／後段效果與保存重播；512px probe 不足 |

### 優先順序、No-Go 重開與交付邊界

1. **先完成本輪紀錄與 Plan。** 停止已知同設定的 ROI／depth gate／prompt／offload 掃參；檔案／權利未知的模型不先下載，不能用重試數換成完成度。
2. **下一輪第一優先：S04 的兩案必要條件篩選。** 理由是這個環境已實際回傳影像，新增 setup 最少；不是已量得最高效益。最強反例正是原先兩個自然移除都難，而單張磚牆不代表它們也可用。計畫要求全結果、停止線、精確 host 合成及人類評估；若失敗即 No-Go 該輪，不增加候選或讓 critic 自我認證。
3. **S07／S06 只在實際痛點／裝置可用時開獨立小輪。** 它們不與 S04 混成品質比較，更不是所有使用者預設。無裝置／人類時間資料就保留 DEFER，不捏造成本排序。
4. **S02／S08 等待新合格 artifact，S09／S10 等待獨立功能需求。** 記錄尚缺 grant、完整鏈、實際成本或背景／alpha 生成能力的具體項目；新 evidence 到齊才重開，不能用更大的研究目錄或換名撤銷原失敗。

每項重開都綁**用途／artifact／版本／裝置／預算／接受標準**；新權利版本只能解權利 blocker，不能順便解畫質或原生數值。新兩案可接受只足以建議擴大資格比較，不通過原 40 場景、三位人類、各平台及 T1–T5；外部或人工模式仍須 owner 明確同意新增 scope。`RetouchStack`、無模型重開與完整產品匯出仍是提案，不因本輪文件存在而算已實作。

本節與新計畫是本輪交付；另同步四份既有相依文件。新分析／文件檢查收據放在 `dist/object-removal-scenario-decision-20261021/`，不覆寫第 14–18 節或 ROI 波次的歷史報告。**沒有新增模型執行、外部請求、產品功能、永久測試或依賴，也不 commit／push。**

