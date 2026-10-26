# Git Blame For Visual Studio

在 Visual Studio 编辑器中，光标所在行自动内联显示 Git blame 信息 —— 无需切换窗口，随时知道这一行代码是谁改的、什么时候改的、为什么改。

![preview](assets/preview.gif)

## 功能特性

- **行内 blame 提示**：光标停在哪一行，行尾自动显示作者、时间、提交说明
- **悬停查看完整信息**：鼠标悬停在 blame 文本上，弹出完整 commit hash、作者、精确时间、完整提交说明
- **左键单击**：复制 commit hash，带视觉反馈

## 环境要求

- Visual Studio 2022 或更高版本（Community / Professional / Enterprise 均可）
- 本机已安装 Git，并确保 `git` 命令在系统 PATH 中可用
- 打开的项目需要是一个 Git 仓库

## 3 种安装方式
- 1、通过插件市场，搜索 GitBlameForVs 安装
- 2、前往 Releases 页面，下载最 .vsix 手动安装
- 3、源码编译，生成产物在 `bin\Release\GitBlameForVs.vsix`

## 许可协议

本项目基于 [MIT License](LICENSE) 开源。

## 贡献

欢迎提 Issue 或 Pull Request。如果这个插件对你有帮助，也欢迎点个 Star。
