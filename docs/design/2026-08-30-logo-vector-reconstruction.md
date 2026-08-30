# ClashSharp Logo PNG 测绘与 SVG 重建记录

- 日期：2026-08-30
- Canonical 输出：`ClashSharp/ClashSharp/Assets/Logo.svg`
- 目标：保留原始 PNG 的六边形比例、`#` 笔画、左下 `C`、45° 长阴影和绿色层次，同时消除旧 SVG 的几何漂移、字体依赖与低尺寸模糊
- 方法：像素边界/颜色分区测量 + 手工构造少量可维护路径；不使用自动 trace，也不使用生成式位图

## 1. 源图分工

| 文件 | 像素 | 背景/用途 | 本次用途 |
|---|---:|---|---|
| `/Logo.png` | 256 × 256 | RGBA 容器，但画面为深绿方形背景 | 检查 256 px 视觉比例和原始发布缩放 |
| `/ClashSharp/Installer/Logo.png` | 1024 × 1024 | 高分辨率深绿方形背景 | 几何、笔画边缘、阴影方向与颜色层次母版 |
| `/ClashSharp/Installer/LogoInstaller.png` | 184 × 184 | 透明背景安装器图标 | 校准六边形透明轮廓和小尺寸留白 |

三张 PNG 不是可以逐像素互换的同一文件：1024/256 版本包含外部深绿背景，184 版本保留透明背景；抗锯齿、阴影透明度和缩放核不同。Canonical SVG 采用透明外部背景，因为它需要在应用、安装器、文档和不同系统表面复用。1024 PNG 的背景不被误画进 SVG。

## 2. 1024-unit 几何测量

SVG 使用 `viewBox="0 0 1024 1024"`，避免把测量坐标再转换为任意设计网格。

### 2.1 六边形

| 特征 | 坐标/范围 | 说明 |
|---|---|---|
| 顶部中心 | `(514, 123)` | 不是画布 `(512, 0)`，顶部保留约 12% 留白 |
| 左上斜边转垂边 | 约 `(176, 346)` | 使用短 Bézier 圆角衔接 |
| 左下垂边结束 | 约 `(176, 663)` | 左右垂边约 317 units |
| 底部中心 | 约 `(514, 889)` | 原图底部抗锯齿范围约 877–889 |
| 右边界 | 约 `x=852` | 与左边界围绕 `x≈514` 对称 |
| 主体 bbox | `x=176…852, y=123…889` | 宽约 676，高约 766；不是规则尖角六边形 |

手工轮廓由 12 个直线/曲线段组成。圆角只用于顶点过渡，不把整体变成旧 SVG 那样膨胀的圆角徽章。

### 2.2 `#` 标记

| 部件 | 范围 | 测量结果 |
|---|---|---|
| 左斜竖画 | top `x=443…508`，bottom `x=370…435` | 向左下倾斜，宽 65 units |
| 右斜竖画 | top `x=598…664`，bottom `x=526…591` | 宽 65–66 units |
| 上横画 | `x=340…713, y=402…469` | 宽 373，高 67 units |
| 下横画 | `x=316…689, y=556…623` | 宽 373，高 67 units |
| 整体可见范围 | 约 `x=316…713, y=290…733` | 下端与 `C` 的 baseline 对齐 |

`#` 使用四个简单多边形而非字体 glyph。这样不会受字体版本、hinting、fallback 或 WPF/浏览器文字布局影响，并能保持原图中“斜竖画 + 水平横画”的独特组合。

### 2.3 左下 `C`

| 特征 | 范围 |
|---|---|
| bbox | `x=275…351, y=649…733` |
| 外轮廓高度 | 84 units |
| 主开口 | 朝右，端点约在 `x=339…351` |
| baseline | 与 `#` 下端 `y≈733` 对齐 |

`C` 由手工轮廓路径构造，不再依赖运行时字体。它在 1024 图中很小，但属于品牌语义，不能像通用 hashtag 图标那样删除。

### 2.4 阴影与表面

- 主要投影方向为 `(1, 1)`，即约 45° 向右下；
- caster 包括两条斜竖画、两条横画和 `C`，所有投影严格裁在六边形内；
- 原 PNG 的重叠投影会形成至少两个深绿层级，并带有栅格模糊/压缩产生的软边；
- SVG 采用独立 caster 路径与统一低透明度深绿，重叠自然加深，不把栅格噪点 trace 成复杂轮廓；
- 主体绿色使用 absolute-coordinate gradient，避免不同渲染器按每个 path bbox 重算渐变。

当前色彩基线为：顶部/左上 `#0C7428`，中段 `#0B7026`，右下 `#0A6822`；长阴影使用 `#033E15`、16% opacity。它们优先保持原 PNG 的明度关系和品牌绿色，不声称与带背景、抗锯齿和旧压缩噪声的 PNG 每个像素相等。

## 3. 旧 SVG 的具体问题

旧 `Logo.svg` 的问题不是“SVG 天生不如 PNG”，而是几何重新想象过度：

- 旧六边形约 `x=124…900`，比 PNG 主体横向膨胀约 100 units；顶部约 `y=104`、底部约 `y=926`，留白和宽高比均漂移；
- 旧轮廓采用大半径连续圆角，原 PNG 的直边与较短角部过渡被削弱；
- `#` 使用一条通用化路径，斜画/横画交叠和端点位置没有与 1024 PNG 对齐；
- 旧 `C` 约延伸至 `x=194…363, y=615…823`，显著大于原始 `x=275…351, y=649…733`，破坏左下视觉重量；
- 高光是一条与 PNG 分区无关的大面积覆盖，长阴影也没有按五个 caster 分解；
- 24 行文件虽短，但短并不等于准确，尤其在 32/64 px 会呈现不同于原图的轮廓和重心。

新 SVG 的改进：

- 主体 bbox、两条竖画、两条横画和 `C` 均直接使用 1024-unit 测量；
- 只有一个 canonical outer path，clip 与 WPF DrawingImage 可复用相同数据；
- 阴影按 caster 组成，重叠透明度自然叠加；
- 外部透明，保留 `<title>`/`<desc>`、`role="img"` 与 `aria-labelledby`；
- 不含 `<text>`、外部字体、位图、脚本、外链或 filter blur，降低跨渲染器差异和包安全面。

## 4. 既有多尺寸预览审查

在 OOM 前已经生成以下临时预览；它们不是仓库发布产物：

- `/tmp/clashsharp-logo-analysis/logo-svg-1024.png`
- `/tmp/clashsharp-logo-analysis/logo-svg-256.png`
- `/tmp/clashsharp-logo-analysis/logo-svg-64.png`
- `/tmp/clashsharp-logo-analysis/logo-svg-32.png`
- `/tmp/clashsharp-logo-analysis/original-vs-svg.png`

人工审查结论：

- 1024/256：六边形比例、`#` 重心、`C` 尺寸和 45° 投影明显比旧 SVG 接近原 PNG；
- 64：所有五个语义部件仍清晰，`C` 开口可辨；
- 32：`#` 清晰，`C` 仍可辨但已接近 1–2 px 笔画极限；
- SVG 在透明背景上比 1024 PNG 看起来更“干净/平”，因为没有把 PNG 外部深绿背景和栅格光晕复制进去；
- 当前版本对原图的立体表面采用克制渐变，尚未把每一块栅格亮面/噪声都矢量化。这是可维护性选择，不是遗漏测量。

16/20/24 px 的 Windows shell icon 应继续使用按尺寸提示过的 ICO frame，不应把 canonical SVG 任意缩小后冒充已经做过像素级 hinting。

## 5. 验证方法与资源约束

所有新渲染必须先通过重型 Linux 资源门禁，并串行运行：

```bash
eng/check-linux-resource-budget.sh heavy -- \
  magick -background none ClashSharp/ClashSharp/Assets/Logo.svg \
  -resize 1024x1024 /tmp/clashsharp-logo-analysis/logo-svg-1024.png
eng/check-linux-resource-budget.sh heavy -- \
  magick -background none ClashSharp/ClashSharp/Assets/Logo.svg \
  -resize 256x256 /tmp/clashsharp-logo-analysis/logo-svg-256.png
eng/check-linux-resource-budget.sh heavy -- \
  magick -background none ClashSharp/ClashSharp/Assets/Logo.svg \
  -resize 64x64 /tmp/clashsharp-logo-analysis/logo-svg-64.png
eng/check-linux-resource-budget.sh heavy -- \
  magick -background none ClashSharp/ClashSharp/Assets/Logo.svg \
  -resize 32x32 /tmp/clashsharp-logo-analysis/logo-svg-32.png
```

验证项：

1. SVG 是 well-formed XML，`viewBox`、title/description 和透明背景存在；
2. 1024/256/64/32 输出尺寸与 alpha 通道正确；
3. outer silhouette 与 184 透明 PNG 等比投影后的 bbox/顶点一致；
4. `#` 和 `C` 的白色区域位置按测量坐标检查，不用整体 perceptual metric 掩盖局部漂移；
5. 逐尺寸人工审查锯齿、细线丢失、阴影粘连和 `C` 开口；
6. WPF 内嵌 DrawingImage 与 SVG 使用相同 path 数据，避免 UI 又出现第三套 Logo。

ImageMagick 的整体 RMSE 不作为唯一质量分数：原 PNG 含外部背景、栅格光晕和抗锯齿，而 SVG 刻意透明且可缩放。量化比较必须先统一 alpha mask、裁剪和色彩空间，并同时报告 silhouette 与内部白色几何，不能对两张语义不同的整幅画布直接排名。

## 6. 后续资产闭环

- [x] 用 1024-unit 手工路径替换几何漂移的旧 SVG；
- [x] WPF shell 使用同一几何 DrawingImage，不再显示临时通用 `C` 图标；
- [ ] 重型资源门禁通过后重新生成四尺寸预览和轮廓指标；
- [ ] 从 canonical SVG 生成并人工提示 16/20/24/32/40/48/64/256 ICO frames；
- [ ] Windows Explorer、任务栏、安装器标题栏在 100/150/200/300% DPI 做人工 smoke；
- [ ] 若未来调整几何，SVG、WPF DrawingImage 和 ICO 生成输入必须在同一变更中更新并由契约测试锁定。
