# `Clash#`

[English](./README.md)

![Clash# Logo](./Logo.png)

`Clash#` 是一个现代化的 Windows 原生代理客户端，基于 [mihomo](https://github.com/MetaCubeX/mihomo) 构建。
`Clash#` 以`AGPL-3.0`协议开源于[GitHub](https://github.com/Water-Run/ClashSharp).

## 关于Windows原生

`Clash#`是Windows原生的. 这不止是技术上使用`C#`+`WinUI3`开发, 契合Fluent的页面设计, `.msix`打包, 还包括其提供的一系列特色功能. 这包括:

- 定制的安装, 卸载管理程序
- 启动时的代理冲突检测和修复
- 异常退出时可选驻留自启动修复服务, 解决常见的关机后重启未关闭手动代理造成无法上网等问题
- WSL, 终端和微软商店的快速网络修正
- 主控页使用类似 Windows 快捷设置的磁贴呈现状态与常用操作

以及其它的有关定制内容.

## 安装与快速上手

### 安装

从 [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases) 下载发布包，解压后运行安装工具。运行`ClashSharp-Installer.exe`(需要管理员权限), 根据指引完成安装.

> 可再次运行`ClashSharp-Installer.exe`修补或卸载程序. 卸载程序也可使用Windows管理.

### 快速上手

欲使用`Clash#`, 显然你需要一个`Clash`订阅.

## 概念

`Clash#`和主流的一些软件有些不同的概念. 大致可以通过以下表格映射:

| `Clash#`中的概念 | 主流软件中的概念 | 说明                    |
|------------------|------------------|-------------------------|
| 主控             | 概览 / 主页      | 核心控制页面            |
| 未激活           | 关闭             | 不开启代理              |
| 待命             | 直连             | 开启代理, 直连模式      |
| 按规则接管       | 规则             | 开启代理, 规则模式      |
| 接管所有         | 全局             | 开启代理, 全局模式      |
| 透明代理         | TUN模式          | 开启代理, 且使用TUN模式 |

> 其中, 透明代理需要在设置中打开

`Clash#`预设的默认端口是`10000`.

## 进阶使用

进阶用户可以配置透明代理、后台连接采样、配置导入与校验、节点延迟测试、Windows 原生修复动作、SQLite 日志清理和中国大陆显示策略。
