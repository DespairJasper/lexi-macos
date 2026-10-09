# 构建与发布

## Windows

开发环境：Windows 10/11 x64、.NET SDK 8、Rust stable（使用 FSRS Rust 库的 edition 2024，至少 Rust 1.85；建议当前稳定版本）。打包另需 NSIS 3 Unicode。NuGet、Cargo 首次还原需要联网。

```powershell
git clone https://github.com/Cofran-77/lexi.git
cd lexi
cargo build --manifest-path windows/native/fsrs-optimizer/Cargo.toml --release --locked
dotnet build windows/Lexi.csproj -c Release
dotnet run --project windows/Lexi.csproj
```

Rust 助手输出为 `windows/native/fsrs-optimizer/target/release/fsrs-optimizer.exe`，桌面项目在文件存在时复制它；打包脚本要求助手已经构建。仓库不跟踪这个二进制文件，但公开 Windows 安装包包含该助手。

生成自包含安装包：

```powershell
pwsh -File windows/packaging/build-installer.ps1 -Version 1.2.4 -Dotnet dotnet
pwsh -File windows/packaging/package-release.ps1 -Version 1.2.4
```

默认输出到仓库根目录 `Lexi-1.2.4-Windows-x64/` 和相应源码/分发 ZIP。Git 工程保留资源格式示例；构建完整专题版本时，先安装当前 Windows 版，将安装目录中的 Assets/IELTS 内容复制到 windows/Assets/IELTS。现有构建规则会复制资源到输出；普通用户直接安装完整 Windows 安装包即可。安装程序需要 NSIS 位于默认路径或通过 `-Nsis` 指定。

### 测试

按改动选择检查，不要求每次都运行全软件测试：领域测试位于 `windows/tests/`，UI/交互自检位于 `windows/SelfTest/`。例如：

```powershell
dotnet run --project windows/tests/MemoryTests/MemoryTests.csproj -c Release
```

某些历史 UI 测试依赖未公开的 IELTS 资源。运行自检必须使用隔离 `LEXI_DATA_DIR`，不要指向个人词库；测试入口与所需资源需先读相应测试源码。本仓库工作流验证当前工程编译，不把编译成功当成全功能验收。

## Android

环境：JDK 17、Android SDK Platform 35、Build Tools 35。工程使用 Gradle Wrapper 8.9、AGP 8.7.3、Kotlin 2.0.21、Compose BOM 2024.12.01。Android Studio 打开 `android/` 即可；本机 SDK 路径放到不提交的 `local.properties`。

```powershell
cd android
./gradlew.bat assembleDebug
```

```sh
cd android
chmod +x gradlew
./gradlew assembleDebug
```

输出：`app/build/outputs/apk/debug/app-debug.apk`。依赖仓库保留工程已有阿里镜像与官方源配置；不需要 macOS 编译环境。

模拟器/真机仪器测试在 `app/src/androidTest`，可按改动执行 `./gradlew connectedDebugAndroidTest`。需要连接设备，不代表 CI 已认证全部 Android / MagicOS 版本。

### 签名与升级

当前 Release 提供 Android 源码与已有开发签名 APK。开发私钥不公开。你本地重新生成的 debug APK 可能使用不同签名，不能保证覆盖已安装的旧 APK。正式分发需要维护自己的长期 release 签名；切换签名前先导出数据，不以卸载冒充无损升级。

## macOS

暂未提交源码，因此没有构建或安装说明。合作作者接入后补齐工具链和签名要求，见 [macos/README](../macos/README.md)。
