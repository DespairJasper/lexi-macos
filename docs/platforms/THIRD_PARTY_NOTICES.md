# 第三方代码与资源说明

根目录 PolyForm Noncommercial LICENSE 只覆盖 Cofran-77 与 DespairJasper 的项目原创代码和文档，不覆盖以下第三方内容。构建后实际分发的依赖也须保留对应通知。

| 内容 | 来源 / 许可 | 仓库中的保留位置 |
| --- | --- | --- |
| ECDICT 常用词子集 | [skywind3000/ECDICT](https://github.com/skywind3000/ECDICT)，MIT | Windows `Notices/ECDICT-LICENSE.txt`；Android `app/src/main/assets/notices/` |
| FSRS Rust 库 | Open Spaced Repetition，BSD-3-Clause | `windows/native/fsrs-optimizer/vendor/fsrs/LICENSE` |
| Avalonia | MIT（部分 native 依赖有独立通知） | Windows `Notices/`；NuGet package metadata |
| .NET、SQLite 等桌面依赖 | 各自许可 | Windows `Notices/` 及项目依赖清单 |
| SkiaSharp、HarfBuzz 与 native 组件 | 各自许可 / 第三方通知 | Windows `Notices/` |
| Inter 字体 | SIL Open Font License 1.1 | `windows/Notices/INTER-LICENSE.txt`，通过 Avalonia.Fonts.Inter 依赖引入 |
| AndroidX / Compose、Kotlin、协程、OkHttp | 通常为 Apache-2.0，按各包发布内容为准 | Gradle 声明与依赖发布包 |
| Gradle Wrapper | Apache-2.0 | Android Wrapper 与 `android/GRADLE-LICENSE.txt` |

## IELTS 学习资料

Windows 配套专题资料来自 [hefengxian/my-ielts](https://github.com/hefengxian/my-ielts)，适配时固定到提交 5cef573933663c4673c6e0093f1df04e68018b1a。上游 README 明确声明禁止商业用途；原书、词汇编排、音频和讲义的权利归各自权利人。Lexi 保留来源信息，不将这些资料归为 Cofran-77 或 DespairJasper 原创，不由根目录许可重新授权。来源声明不等于原书权利人的授权证明。

资料随当前 Windows 安装包及专题资源 ZIP 提供。详见 [资源说明](docs/RESOURCES.md) 与 windows/Notices/MY-IELTS-SOURCE.md。第三方依赖许可与通知继续分别保留。

## 平台合作与许可

macOS 参考项目 [DespairJasper/lexi-macos](https://github.com/DespairJasper/lexi-macos) 由合作作者维护，本仓库尚未导入该端源码。原创内容当前按 PolyForm Noncommercial 分发；许可适用范围与沿革见 LICENSE-POLICY.md，不自动修改外部仓库或第三方许可。
