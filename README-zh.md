# `Clash#`  

[English](./README.md)  

![Clash# Logo](./Logo.png)

`Clash#` 是一个现代化的 Windows 原生代理客户端，基于 [mihomo](https://github.com/MetaCubeX/mihomo) 构建。软件仅适配 Windows 11 x64，并通过原生 Clash# 安装工具部署 MSIX 包。

## 安装

从 [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases) 下载发布包，解压后运行安装工具。发布包包含 `ClashSharp-Installer.exe` 及同级 `payload` 目录。

安装工具会检查 Windows 11 x64 环境，按需安装包证书，并部署 MSIX 包。如果软件已安装，安装工具会进入维护模式，用于检查、通过当前 payload 重新部署修补，或卸载。

维护者可运行 `Tools\Update-Mihomo.ps1` 获取或刷新 Windows x64 mihomo 核心。发布构建会在缺失时自动下载核心，并在打包前执行版本探测。

## 适配 Windows 的特色功能

Clash# 使用原生 WinUI 3 控件、Fluent 图标和 Windows 11 亚克力设计。产品语言围绕 Windows 网络行为设计，而不是直接暴露跨平台代理术语。

Windows 特色功能包括 WSL 网络修正、终端代理诊断、Microsoft Store 网络修正、异常关闭后的代理残留清理、退出时还原系统代理，以及透明代理不可用时回退到系统代理。

## 基础使用

在 **主控** 中切换 Clash# 的状态：未启用、待命、按规则接管、接管所有。

在 **代理** 中管理节点、配置、订阅链接和规则。在 **统计数据** 与 **日志** 中查看基于 SQLite 持久化保存的流量记录、规则命中和日志占用。

## 进阶使用

进阶用户可以配置透明代理、后台连接采样、配置导入与校验、节点延迟测试、Windows 原生修复动作、SQLite 日志清理和中国大陆显示策略。

中国大陆显示默认开启。它只在界面显示层替换地区文本和旗帜，不修改配置、日志、搜索、复制或导出数据。

`Clash#` 在 [GitHub](https://github.com/Water-Run/ClashSharp) 上以 `AGPL 3.0` 协议开放源代码。
