# AI 物件移除：模型、畫質與操作 Survey

- 查核日期：2026-10-20。用途：**研究／比較與實作選型提案，不是已支援功能**。
- 結論：把「選取」與「重建」分開；**目前不採用任何候選作為通用物件移除的產品預設，T1 維持 NO-GO**。MI-GAN 保留快速工程對照，EfficientSAM 僅作待人工修正的選取提案，Moebius 暫緩；沒有量測出「最佳畫質」模型。既有[續驗決策](#10-續驗與採用決策)、[來源續查](#11-權重來源與後續研究)及[場景微調／VAE 內容實驗](#12-場景微調vae-身分與原生續驗)仍成立；新增[多角色替代方案與實驗](#13-多角色替代方案與實驗)只採用研究對照，不改產品需求。
- 已做：模型／權重來源調查、完整鏈續驗、VAE 內容比較，以及四個平行角色的替代方案調查、OpenCV 古典對照與 .NET RGB16 donor probe。未做：40 場景人類盲評、Windows／iOS 移除推論、付費 API、私人照片上傳或產品功能實作。**第 13 節記錄研究期間整個 `dist/` 消失及 v2 重新凍結資料的邊界；舊收據目前不可讀，不宣稱仍可原地重現。**
- 配套：[ADR 0005](adr/0005-ai-object-removal.md) 記錄取捨；[操作與資料契約](object-removal-design.md) 固定 UX／色彩與保存邊界；[實作計畫](superpowers/plans/2026-10-20-ai-object-removal.md) 提供 T1–T5 與 OR-01–OR-14 對照。

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

