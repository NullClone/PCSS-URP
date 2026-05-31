# PCSS URP — URP 非破壊・物理ベース ソフトシャドウ + アニメ調Ramp

URP 17.x (Unity 6.x / RenderGraph) 向けの、**URPコアを一切改変しない** PCSS
(Percentage-Closer Soft Shadows) 実装です。Compute Shader でスクリーン空間に
シャドウマスク `_CustomScreenSpaceShadowmap` を生成し、レシーバー側でアニメ調の
Ramp グラデーション色を合成できます。

対象: **Directional Light（メインライト＝太陽光）専用**。Point / Spot ライトの影には
反応しません（後述 §6）。

> **設計方針:** 物理ベース（太陽の角直径）＋偵察ゲーティング＋TAAデノイズ。
> 空間ボカシ（バイラテラル）は持ちません。ノイズはPoissonカーネルをTAA前提で
> 時間軸にジッタし、ゲームのTAA（時間蓄積）で解消します。**URPのTAA有効を推奨**。
> TAAを使わない場合は `Use Temporal Jitter` を OFF にしてください（静的ディザに退避）。

---

## 1. このツールが目指すもの

- 遮蔽物からの距離に応じて滑らかにボケる物理的ソフトシャドウ（PCSS）。
- **接地部（根元）はカッチリ硬く、遠く伸びた影の先端は柔らかい**（コンタクトハードニング）。
- 自遮蔽による縞模様（シャドウアクネ）やサンプリングノイズが出ない。
- 影の半影エリアに対し、Ramp テクスチャで自由にグラデーション色を付けられる。

---

## 2. 構成ファイル

責務ごとに分割されています。

### C# (`Runtime/`)

| ファイル | 役割 |
|---|---|
| `PCSSFeature.cs` | `ScriptableRendererFeature` 本体（薄いシェル：`Create`/`AddRenderPasses`/`Dispose`） |
| `PCSSSettings.cs` | インスペクタ設定（`[Serializable]`） |
| `PCSSShaderIDs.cs` | C#↔Compute の名前契約（カーネル名 + 全 `_PCSS_*` / URPグローバルID）を集約 |
| `PCSSRenderPass.cs` | RenderGraph Compute パス本体（`RecordRenderGraph` / `ExecutePass` / `PassData`） |
| `PCSSDebugMode.cs` | デバッグ表示モードの enum（None / ReconMask / ScreenSpaceShadow） |
| `PCSSDebugPass.cs` | デバッグ表示パス（`PCSSResources` ContextItem + 画面全体への Blit） |
| `PCSS.Runtime.asmdef` | URP / Core ランタイムへの参照（public APIのみ＝非破壊） |

### Shaders (`Shaders/`)

| ファイル | 役割 |
|---|---|
| `GenerateSSShadowmap.compute` | カーネル2本（`CSReconnaissance` / `CSMainPCSS`）のみ。include で構成 |
| `PCSSInput.hlsl` | 定数・リソース・URPグローバル・`_PCSS_*` 宣言（データ契約） |
| `PCSSCommon.hlsl` | 復元・カスケード・バイアス・ノイズ等の共有ヘルパ |
| `PCSSSampling.hlsl` | 物理PCSSコア `SampleCascadePCSS` |
| `PCSS_Receiver.hlsl` | Shader Graph 用 Custom Function（マスク取得 + Ramp合成 + 光のにじみ） |
| `PCSS.shadergraph` | レシーバーのサンプル Shader Graph |
| `PCSSDebug.shader` | デバッグ表示用フルスクリーンBlit（R成分をグレースケール出力） |

include 依存は `compute → PCSSSampling → PCSSCommon → PCSSInput` の一方向（循環なし）。

---

## 3. 仕組み（パイプライン全体の流れ）

```
URP メインライトのシャドウ描画
  └─ シャドウアトラス(_MainLightShadowmapTexture) と
     グローバル変数(_MainLightWorldToShadow, _CascadeShadowSplitSpheresN …) を生成
        │  + 深度プリパス(_CameraDepthTexture)
        ▼  ← PCSSFeature が AfterRenderingPrePasses で割り込む（1パス / 2 Dispatch）
   ┌──────────────────────────────────────────────────────────┐
   │ Kernel0 CSReconnaissance（偵察 / 1/4解像度）               │
   │   4x4ブロック代表点で周辺3x3を間引き偵察                    │
   │   → 3値マスク _PCSS_ReconMask                              │
   │      1.0=日向 / 0.0=日陰 / 0.5=半影境界                     │
   └────┬─────────────────────────────────────────────────────┘
        │  （Unityが UAV→SRV バリアを自動挿入）
        ▼
   ┌──────────────────────────────────────────────────────────┐
   │ Kernel1 CSMainPCSS（本番 / フル解像度）                    │
   │   1) 偵察マスクをバイリニア参照（補間で境界を自動的に太らせる）│
   │   2) Early-Out（>0.9 日向 / <0.1 日陰 は重い計算をスキップ）  │
   │   3) 半影画素のみ: Blocker検索 → 物理半影 2·Δd·tan(α/2)     │
   │      → 可変半径PCF → カスケード境界ブレンド → 強度/距離フェード│
   └────┬─────────────────────────────────────────────────────┘
        ▼
  _SSShadowResult を _CustomScreenSpaceShadowmap としてフレーム内グローバル公開
        │
        ▼
  レシーバーマテリアル(Shader Graph)が ScreenUV で読み取り、Ramp合成
        │
        ▼
  画面のTAAが時間ジッタを蓄積し、粒子のない滑らかなグラデーションに収束
```

### 3.1 なぜ「非破壊」なのか
URP は毎フレーム、メインライトのシャドウ情報を**グローバルシェーダ変数**として
設定します（`MainLightShadowCasterPass`）。本ツールはそれを Compute Shader 側で
宣言して**読み取るだけ**で、URPのソースには一切手を入れません。出力も独立した
グローバルテクスチャ `_CustomScreenSpaceShadowmap` なので、URP標準の影と共存します。

### 3.2 偵察パス（Kernel0 / CSReconnaissance / 1/4解像度）
4x4ブロックの代表1画素ごとに、深度→ワールド→カスケード→シャドウ座標へ変換し、
周辺3x3を間引きサンプリング。結果を **1.0=完全な日向 / 0.0=完全な日陰 / 0.5=半影**
の3値マスクに分類して `_PCSS_ReconMask`（1/4解像度）へ書き込みます。
半影境界の「太らせ」は明示的な膨張処理ではなく、本番パスがこのマスクを**バイリニア
参照**する際の補間で自動的に行われます（0.5テクセルが周囲約1テクセルへ滲む）。

### 3.3 本番PCSSパス（Kernel1 / CSMainPCSS / フル解像度）
1. **ワールド座標復元**：ScreenUVと`_CameraDepthTexture`、逆ViewProj(`_PCSS_InvViewProj`)
   でワールド座標へ。reversed-Z / UV原点差は `_PCSS_ReversedZ` で吸収。
2. **Early-Out**：偵察マスクをバイリニア参照し、`>0.9`は日向で即`1.0`、`<0.1`は
   日陰として重いループを回避（高速化の要）。
3. **Blocker検索**：`Blocker Search Radius`（小）の範囲を 8-tap Poisson Disk で探索し、
   受光面より光源側にある遮蔽物の**平均深度** `avgBlockerZ` を求める。
4. **物理半影サイズ**：太陽は方向ライトなので「光源位置」が無い。代わりに**角直径 α**
   から半影幅を物理的に算出する：

   ```
   penumbra(world) = 2 · Δd · tan(α / 2) · penumbraScale
   Δd = |receiverZ − avgBlockerZ| × worldPerDepth   （正射影シャドウ深度から復元したワールド距離差）
   ```

   `worldPerDepth` / `worldPerUV` は `_MainLightWorldToShadow[cascade]` の行ベクトル長
   から導出（カスケード包囲球サイズ・アトラスのタイル分割を内包する非破壊な算出）。
   - 根元（Δd≈0）→ 半影ほぼ0 → シャープ
   - 影の先端（Δd大）→ 半影が広い → ボケる
   - **`Far Softness`** で先端ほどボケる効果をさらに強調（根元のシャープさは維持）。
5. **可変半径PCF**：求めた半影幅をアトラスUV半径へ変換し（`Max Filter Radius`で上限
   クランプ）、8-tap Poisson で再サンプリングしてソフト度(0〜1)を算出。
6. **カスケード境界ブレンド**：包囲球の縁付近(`Cascade Blend`幅)で隣の遠いカスケードを
   もう一度評価し lerp で滑らかに繋ぐ。
7. **強度 + 距離フェード**：`_MainLightShadowParams` から影の強度と距離フェードを適用。

### 3.4 ノイズ対策（バイラテラル廃止 → ブルーノイズ + TAA）
空間ボカシ（バイラテラルフィルタ）は**廃止**しました。代わりに：
- **空間シード**を 2×2 ブロックでロック（ブルーノイズ未設定時は IGN にフォールバック）。
- **時間シャッフル**：フレームごとに Poisson の回転角を黄金比でずらす。
- 画面の **TAA** が successive frame を蓄積し、粒子のない滑らかなグラデーションへ収束。

→ TAAが前提です。TAAを使わない場合は `Use Temporal Jitter` を OFF にしてください
（フレーム間ジッタを止め、静的ディザになります）。

### 3.5 レシーバー側の合成
`PCSS_Receiver.hlsl` の Custom Function で `_CustomScreenSpaceShadowmap` を ScreenUV
で読み、0〜1のマスクを Ramp テクスチャの U 座標に使う。U=0が影色、U=1が光（通常白）。
`FinalColor = BaseColor × ShadowColor` でアニメ調の境界グラデーションになる。

**光のにじみ (Light Bleed)** — `PCSS_ShadeAnimeRamp_float` を使うと、Ramp合成に加えて
明暗境界(半影)へ「光が影側へ侵入する」発光バンドを足せる。原神/チェンソーマン的な
color1(光側)/color2(影側) + 境界の高輝度ふち を再現できる。

| 入力 | 役割 |
|---|---|
| `BaseColor` | アルベド×ライトカラー等のベース |
| `RampTex` / `RampSS` | 0(影)〜1(光) を U とするRamp |
| `BleedColor` | にじみ(発光)の色（光の透過色など） |
| `BleedPosition` | にじみ帯の中心（マスク値基準。境界≒0.5） |
| `BleedWidth` | にじみ帯の幅 |
| `BleedIntensity` | にじみの強さ（加算量） |

出力は `ShadowMask`（0〜1）/`RampColor`/`FinalColor`。にじみは加算なので Bloom と
併用すると“光が漏れる”質感が際立つ。

---

## 4. セットアップ手順

1. **RenderGraph モード**であること（Project Settings > Graphics > URP の
   "Compatibility Mode" が **OFF**）。本実装は RenderGraph 専用で、Compatibility Mode
   下では何も描画しません。
2. **深度テクスチャを有効化**（Renderer Asset で Depth Texture ON。
   本機能も `ConfigureInput(Depth)` で自動要求します）。
3. **Renderer に Feature を追加**：Renderer アセットの *Add Renderer Feature* →
   **PCSS Feature**。
4. **Compute Shader を割り当て**：Feature の *Compute Shader* に
   `Shaders/GenerateSSShadowmap.compute` をドラッグ。
5. レシーバーの Shader Graph に `Shaders/PCSS_Receiver.hlsl` の Custom Function を
   組み込み、`Screen Position`(Default) と Ramp テクスチャを接続
   （サンプルは `Shaders/PCSS.shadergraph`）。
6. **TAA を有効化**（推奨）。使わない場合は Feature の `Use Temporal Jitter` を OFF。

---

## 5. パラメータ一覧

| パラメータ | 役割 | 上げると |
|---|---|---|
| **Sun Angular Diameter** | 太陽の角直径 α（ラジアン）。半影の物理的な広がりの主役。既定 0.0093 ≒ 実際の太陽 | 影全体が柔らかくなる |
| **Penumbra Scale** | 物理半影サイズの芸術的乗数（1=物理どおり） | 誇張したソフト影になる |
| **Blocker Search Radius** | 遮蔽検索半径（テクセル単位） | 半影の検出範囲が広がる／接地判定が緩む |
| **Max Filter Radius** | フィルタ半径の上限（アトラスUV単位） | **遠方の影が更にボケる**（頭打ちが外れる） |
| **Far Softness** | 影の先端ほどボケを追加強調 | **遠く伸びた影だけ強くボケる**（根元は維持）。0=物理線形 |
| **Depth Bias** | 自遮蔽対策の基本深度バイアス | 縞模様が消える（上げ過ぎでピーターパン現象） |
| **Slope Bias** | 傾斜(receiver-plane)バイアス係数。光が低いほど自動で強化 | 大フィルタ時の縞模様・突変エッジが消える。0で無効 |
| **Cascade Blend** | カスケード境界のブレンド幅 | 継ぎ目が滑らかになる。0でハード切替。境界付近のみ2回計算しコスト増 |
| **Stabilize Sampling** | サンプル原点をシャドウテクセル中心へスナップ | カメラのサブテクセル移動による影のチラつき(Shadow Swimming)を抑制 |
| **Use Blue Noise** | 回転角ディザにブルーノイズを使用 | 少サンプルでもバンディング/ノイズが減る。未設定/OFFはIGNに自動フォールバック |
| **Use Temporal Jitter** | フレーム毎に回転角を時間シャッフル | **TAA前提**でノイズ解消。TAA非使用時はOFF（ちらつき防止） |
| **Blue Noise Texture** | ブルーノイズのソーステクスチャ | `.r`成分を回転角に使用 |
| **Debug Mode** | 画面全体へのデバッグ表示モード | None=通常 / ReconMask=偵察マスク(1/4) / ScreenSpaceShadow=最終マスク |
| **Debug Shader** | デバッグ表示用シェーダ | `Shaders/PCSSDebug.shader` を割り当て（Debug Mode 使用時のみ必要） |

### Blue Noise テクスチャの設定
タイリング可能なグレースケールのブルーノイズ（できれば STBN）を割り当てます。Import設定は
**sRGB(Color Texture) OFF（Linear）/ Filter=Point / Mip無し / 圧縮なし** を推奨。`.r` 成分のみ使用。
未設定または OFF の場合は `InterleavedGradientNoise` にフォールバックします。

### 太陽のリアリズムについて
既定の `Sun Angular Diameter = 0.0093`（実際の太陽）は**シャープな影**を生みます（仕様）。
スタイライズした柔らかい見た目にしたい場合は、ボカシ半径ではなく **Penumbra Scale**
（芸術的乗数）または **Sun Angular Diameter** を上げてください。

### Texel Snapping（Stabilize Sampling）の実装位置について
本実装は **URPが既に描画したアトラスのスクリーン空間リーダー**です。Texel Snapping は
C#でカスケード行列を再スナップするのではなく、**シェーダ側でサンプル原点をテクセル中心へ
スナップ**して行います。URPは自身のカスケードを描画時に既にスナップ済みであり、こちらで
行列を再スナップするとアトラスと**desync**して誤差を生むためです。

---

## 6. なぜ Directional Light 専用か（仕様）

内部で使うデータがすべてメインライト用だからです：
`mainShadowsTexture` / `_MainLightWorldToShadow` / `_CascadeShadowSplitSpheresN`。
Point / Spot の影（`additionalShadowsTexture`）はサンプリングしていません。
太陽光の高品質ソフトシャドウという用途では、これが**最適でコストも軽い**構成です。

---

## 7. 技術メモ / 既知の前提

- PCSS は URP がグローバル設定するシャドウ変数を Compute 側で読み取る方式（非破壊）。
  万一これらがバインドされない環境では影が出ません。その場合はメインライトのシャドウ
  設定（カスケード/距離）と Feature の有効状態を確認してください。
- 出力 `_CustomScreenSpaceShadowmap` は `SetGlobalTextureAfterPass` でフレーム内グローバル
  公開され、不透明/半透明パスのマテリアルから参照可能。
- パスは `AllowPassCulling(false)`（消費側がグラフ外マテリアルのため）、
  `AllowGlobalStateModification(true)` + `UseAllGlobalTextures(true)`（URPグローバル変数の
  読み取りのため）で構成。
- Reversed-Z / UV原点はプラットフォーム差を `_PCSS_ReversedZ`（C#から `SystemInfo.
  usesReversedZBuffer` で設定）で吸収。`IsBlocker` の比較方向・空判定・Y反転で共有。
- **8×8 不変条件**：スレッドグループは `CeilToInt(dim/8)` で `[numthreads(8,8,1)]` に対応。
- 早期サイレント終了が3箇所：`computeShader == null`、Preview/Reflectionカメラ、
  メインライトシャドウ/アトラス/カメラ深度が無効なとき。Frame Debugger で
  **"Advanced PCSS (Screen Space)"** パスを確認できます。

---

## 8. デバッグ「何も起きない」とき

1. Compatibility Mode が OFF（RenderGraph）か。
2. Renderer で Depth Texture が ON か。
3. Feature に Compute Shader が割り当て済みか。
4. メインライトのシャドウが有効でカスケードが描画されているか。
5. Frame Debugger に "Advanced PCSS (Screen Space)" パスが出ているか。
   `_CustomScreenSpaceShadowmap` の中身を目視で確認。

### 8.1 画面全体へのデバッグ表示（Debug Mode）
中間バッファを Scene / Game ビューに直接オーバーレイ表示できます。

1. Feature の **Debug Shader** に `Shaders/PCSSDebug.shader` を割り当てる。
2. **Debug Mode** を選ぶ：
   - **ReconMask** … 偵察マスク（1/4解像度）。白=日向 / 黒=日陰 / 灰=半影(0.5)。
     1/4解像度なのでブロック状に拡大表示されます（偵察の分類が直接見える）。
   - **ScreenSpaceShadow** … 最終マスク `_CustomScreenSpaceShadowmap`（フル解像度）。
   - **None** … 通常描画（オーバーレイ無し）。
3. 通常に戻すには Debug Mode を **None** へ。

表示は **ポストプロセス後（`AfterRenderingPostProcessing`）** に Blit するため、シーン描画で
上書きされません。偵察マスクは Compute パスが早い段階で生成するので、ハンドルを
`PCSSResources`（`ContextItem`）でフレーム内共有し、遅い段階のデバッグパスが読みます。
グレースケールは R 成分の値そのもの（Blitter 規約で向きの差異は吸収）。

---

## 9. 関連ドキュメント

- `README_References.md` — アルゴリズムの元ネタ記事（CN/TW）。blocker/penumbra/cascade-blend
  の数学的背景。ただし旧8カスケード・パイプライン改変型を記述しており、本非破壊実装とは
  構成が異なる点に注意。
- `URP Advanced PCSS Implementation Guide.md` — 当初の要件定義書（歴史的経緯）。冒頭の
  注記参照。現行実装はこれから発展しているため、**コードを正とする**こと。
