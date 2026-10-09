# Lexi for Android

Kotlin / Jetpack Compose 原生应用，面向 Android 8.0 及以上手机和平板，当前版本 0.2.2-alpha。作者 Cofran-77、DespairJasper。

## 功能

- ECDICT 离线查词，个人词汇档案、编辑与批量管理。
- 今日重逢：先回忆再揭晓，采用第 1、2、4、7、15 天固定日期复习排期。
- 按需生成例句、近反义词与常用词组，支持用户自行配置 AI 接口。
- 系统 PROCESS_TEXT 选词入口与分享文字接收。
- CSV 词条迁移、兼容旧 JSON、本端 SQLite 备份与恢复。
- A4 PDF 导出、系统打印，支持手机、平板与分屏布局。

Android 当前不提供 Windows 的完整每日计划、FSRS-6、全局专注和金句本功能集。两端数据库不能直接互相覆盖。

## 安装与升级

[Releases](https://github.com/Cofran-77/lexi/releases) 提供可直接安装的 0.2.2-alpha APK，沿用已有开发签名。同签名更新可覆盖安装；自行构建的 APK 可能签名不同，系统会拒绝覆盖。卸载会删除应用私有数据，先导出或备份。预览版不代表已完成所有真机与系统版本适配。

PDF 默认保存到下载目录的 Lexi词汇库，支持打开与分享；Android 8–9 可能需要存储权限。CSV 文件可由用户选择保存位置。备份恢复会替换本端词库，请先保存当前数据。

## AI 与隐私

离线查词不调用 AI。生成请求由用户触发，发送当前词与所选模块，来源原句仅在相应选项开启时附带。AI 内容需明确保存才会写入档案。

API Key 默认仅保存在本次运行内存；用户选择记住后使用 Android Keystore AES-GCM 加密。密钥不进入 CSV、PDF 或 SQLite 备份。应用不提供云账号或自动同步。

## 构建

JDK 17、Android SDK 35、Build Tools 35；Gradle Wrapper 8.9、AGP 8.7.3、Kotlin 2.0.21。Android Studio 打开本目录，或执行：

```sh
chmod +x gradlew
./gradlew assembleDebug
```

Windows 可使用 `gradlew.bat assembleDebug`。APK 输出到 app/build/outputs/apk/debug/app-debug.apk。本机 SDK 路径和签名私钥不应提交。

## 许可

当前源码按本目录 PolyForm Noncommercial 1.0.0 分发；历史 MIT 源码与既有 APK 的授权不追溯更改，见 [许可政策](../docs/platforms/LICENSE-POLICY.md)。第三方许可保留在 app/src/main/assets/notices。后续商业使用或重新签名分发须分别处理许可与签名权利。

## 来源

本端源码复制自 [Cofran-77/lexi](https://github.com/Cofran-77/lexi)，基线提交 68becc1。作者 Cofran-77、DespairJasper。安装包和更新记录由来源仓库提供。本目录许可见 LICENSE，第三方声明见 docs/platforms。
