# 巫妖的远程魔导控制系统

[下载 Windows 安装包](https://github.com/mohsunil/owndesk/releases/download/v0.2.2/OwnDesk-setup-0.2.2.exe) · [查看版本说明](https://github.com/mohsunil/owndesk/releases/tag/v0.2.2)

下载文件名为 `OwnDesk-setup-0.2.2.exe`，安装界面和桌面快捷方式名称为“巫妖远控”。

简称“巫妖远控”，内部产品名 OwnDesk。Windows 10 / 11 x64 家庭远控试用版，使用未修改的官方 RustDesk 引擎与 Tailscale 个人专网。

在每台电脑运行发布页的 EXE 安装包，首次登录同一专网并用连接码配对，以后从设备列表连接。你和家人可以各自控制不同的电脑。软件免费；专网服务的个人免费方案以 Tailscale 当前规则为准。

首页可以给本机起名，设备卡片可直接改名。新连接码携带本机名称，重新配对保留已有备注。

管理器支持高清画质预设、设备管理、配对信息的 Windows 用户加密、签名自动更新及失败回退。安装器会请求 Windows 管理员许可来安装和配置官方组件。

这是家庭试用版。本机检查与隔离目录内的真实升级启动测试已通过；本机安装修复与连接码配置已验证；其他电脑首次安装、异地画质、不同 VPN 并用、两人同时远控尚待目标设备实测。未经独立安全审计，管理器与安装器暂无商业 Authenticode 签名。账号和设备本身失陷仍会影响安全。

QQ 图片剪贴板和文件复制已启用；本机 QQ 截图直接拖入远端网页上传区域尚未实现。不要把图片粘贴或文件传输视为已完成的跨电脑网页拖放。

## 构建

安装 .NET Framework 4.8 或更高版本，使用 Windows PowerShell 运行 `build.ps1`。系统 C# 编译器生成管理器与更新助手，无 NuGet 依赖。安装器源码在 `installer/`；`build-setup.ps1` 将完整程序目录编入单个 EXE。

`OwnDesk.exe --self-test <报告绝对路径>` 运行本地检查。更新描述用 RSA PKCS1 SHA256 签名，客户端预置公钥。发布私钥、配对口令、用户设备名单不包含在仓库或公开包中。

## 第三方与许可

OwnDesk 管理器及安装器源码使用 MIT 许可。RustDesk 是独立、未修改的官方 AGPLv3 可执行文件，其许可和对应源码档案随完整安装包提供。Tailscale 官方 MSI 随完整安装包提供并校验；登录与服务适用其官方条款。

本项目与 ToDesk、RustDesk、Tailscale、Microsoft 无隶属关系。详细版本、哈希和说明见 `THIRD-PARTY-NOTICES.txt` 及安装包中的使用说明和测试记录。


