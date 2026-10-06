# mihomo 固定源码材料与离线编译

10 月 6 日继续 G09 的发行准备，为现有 `v1.19.27` 内核保存准确的上游产物、源码、依赖、工具链及公开 CA 输入，并完成离线交叉编译。没有更换产品中的内核，Clash# 版本保持 `1.0.0.0`。

## 准确产物与输入

内置 `mihomo.exe` 的 SHA-256 为 `842fa17493a82c97148e76e3c523f5058e3cf386fba611cab86b6681e75f2f77`，47,213,056 字节，与上游 [`mihomo-windows-amd64-v1-v1.19.27.zip`](https://github.com/MetaCubeX/mihomo/releases/download/v1.19.27/mihomo-windows-amd64-v1-v1.19.27.zip) 中的程序逐字节相同。该归档的资产 ID 为 `440046622`，SHA-256 为 `0f67ea6452545a5bbd192def9456ab970aedcbeea27161a04b764eb680927af2`。无 `v1` 后缀的 amd64 归档是另一个产物，本次首次比对的不匹配结果保留在证据目录。

只读 `go version -m` 确认内置程序使用 `go1.26.4`、`GOAMD64=v1`、`CGO_ENABLED=0`、`with_gvisor`，源提交为 `5184081ac327394d9e15fa5d5f9f4a61e723fd94`。上游标签也解析到该提交；构建信息标记了修改后的工作树。

| 材料 | SHA-256 |
| --- | --- |
| [固定提交源码归档](https://codeload.github.com/MetaCubeX/mihomo/tar.gz/5184081ac327394d9e15fa5d5f9f4a61e723fd94) | `bf3a188a83475000df235178edf61cd70fda22b884b19a539d0cfd9b89a51e6a` |
| [发布附带的 vendor.tar.gz](https://github.com/MetaCubeX/mihomo/releases/download/v1.19.27/vendor.tar.gz) | `9e570d47209c1e7f2578d8d83004f3e8eeb393bba7b3e8bb096c3ca0a2582403` |
| [发布附带的 toolchain.tar.gz](https://github.com/MetaCubeX/mihomo/releases/download/v1.19.27/toolchain.tar.gz) | `8250d606febf9d42bec63040bcdf9399f4c310c05076df870eea9829669ec19d` |
| 内置程序中的公开 CA 数据块 | `6d84ab71cb726c0641b0af84303c316e3fa50db941dc8507d09045eb2fa5d238` |

两个发布归档的长度及摘要均与 GitHub 资产元数据一致。源码归档绑定不可变提交，保留原始字节与本地摘要。

## CA 输入与构建结果

该提交的 `component/ca/ca-certificates.crt` 为空；[上游发布流程](https://github.com/MetaCubeX/mihomo/blob/5184081ac327394d9e15fa5d5f9f4a61e723fd94/.github/workflows/build.yml) 会在编译前替换为构建机器的公开 CA 集合，源代码通过 `go:embed` 引用。仅提供标签源码和 vendor 会遗漏这一实际输入。

在准确内置二进制偏移 `45152288` 发现唯一的大型连续 PEM 数据块，219,342 字节，包含 146 张可解析的公开证书。保存该块并记录摘要，将它作为观察到的 CA 输入加入验证树。没有向 Windows 证书存储导入这些证书。

Linux x86_64 上使用发布附带的工具链和 vendor，设置 `GOPROXY=off`、`GOSUMDB=off`、`GOTOOLCHAIN=local`，以两个构建工作线程完成 Windows/amd64/v1 交叉编译。01:45:26–01:46:24 UTC 的验证成功，117 项依赖的路径和版本与内置程序一致。新编译的 Windows 程序没有被执行。

本次使用独立构建时间并省略 VCS 元数据，输出摘要为 `ebb9a39a9ea53771499447c03b96e155c9b7502097ac08062e0a3df1cf1ac4f9`，不宣称二进制逐字节复现，也不将构建成功直接等同于最终发行材料全部完成。

## 保存与后续

`artifacts/verification/release-source-20261006/` 保存 API 元数据、原始归档、构建信息、CA 观察、验证脚本和离线编译收据。整理的内部源码材料 ZIP 包含 12 项文件，81,946,783 字节，SHA-256 为 `4634D4E20CD36FE616D92C4D389099308726D66C239479224F18B1CC8D937CA3`；逐项重新读取归档并比对原文件摘要，全部一致。

正式发行仍需把这些固定输入接入可重复的源码材料生成与发布流程，并完成 GeoData 上游输入追溯及最终候选的平台验收。正式签名仍按用户决定随后配置。本次内部材料包没有公开发布。
