# QualcommConsole 项目说明与编译指南

## 1. 项目定位
`QualcommConsole` 是一个基于 .NET 的 Qualcomm EDL（Emergency Download）控制台刷机工具，核心目标是：
- 在 EDL 模式下完成端口连接、Sahara 握手、Firehose 配置
- 读取 GPT 分区表、分区读写、擦除、批量刷写（rawprogram）
- 支持 Oplus/OnePlus/Xiaomi 等认证流程与设备信息提取
- 支持云端 Loader 匹配与本地 Loader 手动连接

项目入口是 `Program.cs`，主要交互通过文本菜单完成。

## 2. 技术栈与目标框架
- 语言：C#
- SDK 风格项目：`Microsoft.NET.Sdk`
- 目标框架：`net9.0-windows`
- 串口库：`System.IO.Ports`
- 默认发布倾向：
- `RuntimeIdentifier=win-x86`
- `SelfContained=true`
- `PublishAot=true`

对应项目文件：`QualcommConsole.csproj`

## 3. 代码架构（按目录）
- `Program.cs`
- 控制台主菜单、参数处理、连接流程调度
- `ConsoleController.cs`
- UI 层与 Service 层之间的控制器；封装连接、读表、读写分区、设备信息展示
- `Services/`
- `qualcomm_service.cs` + partial 文件：连接状态机、认证、分区操作、诊断操作
- `device_info_service.cs`：从 super/物理分区提取 build.prop 与设备属性
- `cloud_loader_service.cs` / `ConsoleCloudLoaderIntegration.cs`：云端 Loader 匹配与下载
- `Protocol/`
- `sahara_protocol.cs`：Sahara 握手与 Loader 上传
- `firehose_client*.cs`：Firehose 配置、分区读写、VIP 模式、GPT 操作
- `Authentication/`
- `new_oplus_auth_strategy.cs`、`oneplus_auth_strategy.cs`、`xiaomi_auth_strategy.cs`
- `Common/`
- 文件系统与分区解析：`erofs_parser.cs`、`ext4_parser.cs`、`gpt_parser.cs`、`lp_metadata_parser.cs`
- 串口与 I/O：`serial_port_manager.cs`
- `Database/`
- Loader 数据库、芯片数据库、签名数据库
- `Tests/`
- `AuthStrategyTests.cs`（认证策略相关测试）

## 4. 关键运行流程（高层）
1. 连接阶段
- 扫描端口 -> 选择 EDL 端口 -> Sahara 握手 -> 上传 Firehose Loader
2. 认证阶段（视机型/模式）
- Oplus new / old、Xiaomi 等认证策略
3. Firehose 阶段
- `Configure` 存储参数
- GPT 读取与分区缓存
4. 功能阶段
- 读取/写入/擦除分区
- 批量刷写 rawprogram
- 读取设备信息（build.prop / super 逻辑分区）

## 5. 编译前准备
在 Windows PowerShell 下执行，建议先确认：
1. 安装 .NET 9 SDK
- `dotnet --info`
2. 若要 AOT，安装 C++ 构建工具
- Visual Studio 2022 Build Tools（包含 MSVC、Windows SDK）
3. 进入项目目录
- `cd D:\G_Dragon\Code\WackClientCore依赖项目\QualcommConsole`

## 6. 每组规范命令（可直接复制）
统一参数（与当前 `bin` 目录结构一致）：
- 目标框架：`net9.0-windows`
- 运行时：`win-x86`
- Build 输出：`bin\{Configuration}\net9.0-windows\win-x86\`
- Publish 输出（默认）：`bin\{Configuration}\net9.0-windows\{RID}\publish\`
- 不使用 `-o .\publish\...`，避免在项目根目录创建 `publish` 文件夹

### 6.0 最简推荐命令（只用这两组）

#### 6.0.1 最简组 1：普通发布（非 AOT）
```powershell
dotnet restore .\QualcommConsole.csproj
dotnet clean .\QualcommConsole.csproj -c Release
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true
```

#### 6.0.2 最简组 2：AOT 发布（NativeAOT）
```powershell
dotnet restore .\QualcommConsole.csproj
dotnet clean .\QualcommConsole.csproj -c Release
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=true -p:SelfContained=true
```

### 6.1 命令组 A：预处理（还原 + 清理）
```powershell
dotnet restore .\QualcommConsole.csproj
dotnet clean .\QualcommConsole.csproj -c Release
```

### 6.2 命令组 B：普通编译 Debug（非 AOT）
```powershell
dotnet build .\QualcommConsole.csproj -c Debug -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true -v minimal
```

### 6.3 命令组 C：普通编译 Release（非 AOT）
```powershell
dotnet build .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true -v minimal
```

### 6.4 命令组 D：普通发布 Release（非 AOT）
```powershell
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true
```

### 6.5 命令组 E：AOT 发布 Release（NativeAOT）
```powershell
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=true -p:SelfContained=true
```

### 6.6 命令组 F：一键标准流程（非 AOT）
```powershell
dotnet restore .\QualcommConsole.csproj
dotnet clean .\QualcommConsole.csproj -c Release
dotnet build .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true -v minimal
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=false -p:SelfContained=true
```

### 6.7 命令组 G：一键标准流程（AOT）
```powershell
dotnet restore .\QualcommConsole.csproj  
dotnet clean .\QualcommConsole.csproj -c Release
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x86 -p:PublishAot=true -p:SelfContained=true
```

### 6.8 命令组 H：产物校验
```powershell
Get-ChildItem .\bin\Debug\net9.0-windows\win-x86\QualcommConsole*
Get-ChildItem .\bin\Release\net9.0-windows\win-x86\QualcommConsole*
Get-ChildItem .\bin\Release\net9.0-windows\win-x86\publish\QualcommConsole*
```

### 6.9 命令组 I：可选 x64 发布
```powershell
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x64 -p:PublishAot=false -p:SelfContained=true
dotnet publish .\QualcommConsole.csproj -c Release -f net9.0-windows -r win-x64 -p:PublishAot=true -p:SelfContained=true
```

## 7. 常用命令

### 7.1 本地运行
```powershell
dotnet run --project .\QualcommConsole.csproj
```

### 7.2 环境检查
```powershell
dotnet --info
dotnet workload list
```

## 8. 说明
- 当前 `QualcommConsole.csproj` 默认启用 `<PublishAot>true</PublishAot>`，普通编译时请显式传 `-p:PublishAot=false`。
- 建议串行执行 `dotnet publish`，不要并行发布，避免 `obj` 文件占用冲突。
