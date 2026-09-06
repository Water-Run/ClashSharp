# Installer 向量资源与 ICO 生成

## 修复与资源来源

Installer 的 WPF 页面使用既有紫色主题，但原有 EXE 图标仍来自绿色位图，且只包含 16 / 24 / 32 / 48 / 64 / 128 帧。
现在 ICO 直接由 `InstallerTheme.xaml` 中实际使用的 `ClashSharpBrandMark` 生成，提供 16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256 帧。
主应用的绿色 `Assets/Logo.svg` 保持为几何母版，Presentation 契约逐层核对六边形、五个阴影和五个白色路径；安装器沿用紫色配色。

原 WPF 将所有白色笔画拼成一个默认奇偶填充路径，四个交叉处因此镂空；五个阴影拼接后也不能保持 SVG 的逐层叠加。
改为独立 GeometryDrawing 后，四处交叉恢复实心白色，阴影按原 SVG 的绘制顺序叠加。离屏旧版/新版比较实际复现并验证了这四处差异。

## 再生成

在 Windows PowerShell 5.1 的 STA 线程运行：

```powershell
powershell.exe -NoProfile -File eng/Generate-InstallerIcon.ps1
powershell.exe -NoProfile -File eng/Generate-InstallerIcon.ps1 -Verify
powershell.exe -NoProfile -File eng/Generate-InstallerIcon.ps1 -Verify -PreviewDirectory artifacts/verification/installer-icon
```

- 只读取可信的仓库向量资源，使用系统 WPF 离屏软件绘制，不创建窗口、不启动安装器。
- 每帧直接按目标尺寸绘制；16–32 px 的两条横笔对齐整像素，保留原斜竖画。
- `C` 保留原路径，16 px 时光学范围至少为 2 × 2 px，20–32 px 时至少为 2 × 3 px，向下对齐以避免与下横笔粘连。最小尺寸仍受像素数量限制，不宣称与大图同等可读。
- 40 px 及以上保留完整母版几何、1024-unit 画布留白和裁剪，不从较大的栅格帧缩小。
- ICO 使用不压缩的 32-bit DIB、straight BGRA 和 DWORD 对齐的 AND mask；透明像素 RGB 归零。不依赖 ImageMagick、PNG 压缩版本、字体或生成时间。
- 预览 PNG 仅供检查，不参与 ICO 的字节生成。`-Verify` 完整比较重新生成的字节与仓库资产，不覆盖目标 ICO。
- CI 在 Windows PowerShell 5.1 中执行 `-Verify`；更换系统渲染器或资源后如有字节差异，必须重新生成并检查，不能跳过检查或自动接受差异。

尺寸选择依据 [Microsoft 的 Windows 应用图标说明](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)。该说明要求至少包含 256 px 帧，并列出不同缩放比例对应的图标尺寸。WPF 对 [RenderTargetBitmap 使用软件绘制](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-direct3d9-interoperation)。

## 验证范围

- 本机 Windows 与远端 Server 2025 分别运行同一生成脚本，得到完全相同的 381038 字节 ICO，SHA-256 为 `c86245d4dcfa50b9ffde36b85f7173fbe5b30d5257a6cbc65a833bca643719bf`。
- Windows `IconBitmapDecoder` 解码全部九种尺寸，每帧 BGRA 与生成时的目标像素逐字节一致，透明角落保留。解码器的帧枚举顺序不是目录顺序，验证按尺寸匹配唯一帧。
- 完整 Release x64 构建 0 警告、0 错误；Presentation 97 项、RepositoryTopology 16 项通过，均 0 跳过；PowerShell 5.1 和 7 均成功解析全部 13 个维护脚本。
- 脱敏收据：`artifacts/verification/installer-icon-validation-m4g.json`；离屏比较与逐尺寸预览位于 `artifacts/verification/installer-icon-m4g`。

这些验证仅涉及代码资源、离屏绘制和 ICO 解码。Windows Explorer、任务栏、安装器窗口及 UAC 在 100 / 150 / 200 / 300% DPI 下的桌面检查仍未完成，不能用离屏预览代替。
