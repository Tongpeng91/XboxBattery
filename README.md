# XboxBattery

Windows 托盘小工具：显示 Xbox 手柄电量，同时支持蓝牙鼠标 / 键盘的剩余电量显示，卡片大小可调。

## 功能

- **手柄电量**：通过 XInput 读取（USB / 官方无线适配器），低电量会响两声提醒；蓝牙直连手柄时系统读不到电量，会显示“蓝牙连接（无法读取电量）”。
- **蓝牙鼠标 / 键盘电量**：已连接的蓝牙鼠标、键盘会在手柄卡片下方各占一行，显示名字、电量条和剩余百分比，连接几个就显示几个（每 15 秒刷新一次，电量读数 60 秒缓存一次）。读不到的设备显示“未知”。
- **卡片缩放**：卡片右下角拖动“⋰”可以放大缩小，也可以在托盘图标右键菜单 →“卡片大小”里选 小 / 中 / 大，调好的大小会记住。
- **开机自启动、窗口置顶**：和旧版一样，第一次运行自动设为开机自启动。

## 运行要求

- Windows 10 / 11
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)（免费，装一次就行）

## 下载

去 [Releases](../../releases) 里下载 `XboxBattery-v2.zip`，解压后双击 `XboxBattery.exe` 运行。先退出正在运行的旧版，不然新版会因为“已运行”而直接退出。

## 从源码构建

需要 .NET 8 SDK：

```bash
dotnet build -c Release
# 或发布 framework-dependent 版本：
dotnet publish -c Release -r win-x64 --self-contained false
```

## 说明

- 蓝牙鼠标 / 键盘的电量能不能读到，取决于设备支不支持蓝牙标准电量上报（GATT Battery Service）。大部分新款蓝牙鼠标键盘都支持；个别设备（比如只走私有协议的）会显示“未知”，这是设备本身不报。
- 手柄部分和旧版完全一样：USB / 官方无线适配器读 XInput 电量，低电量蜂鸣提醒。
