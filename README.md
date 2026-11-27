# SearchBar_Edwin
Windows 搜索工具（自用）。默认按 `Alt + Space` 唤起搜索框，输入一次就能同时查本地词典和搜索文件。

Windows x64 提供 [中英词典版](https://github.com/EdwinJunyu/SearchBar_Edwin/releases/tag/v0.2.2-zh-en) 和 [中日词典版](https://github.com/EdwinJunyu/SearchBar_Edwin/releases/tag/v0.2.2-zh-ja)。中英版内置两本中英辞书，中日版内置四本中日辞书。两版的程序功能相同。解压 ZIP 后打开 `SearchBar_Edwin-win-x64/SearchBar_Edwin.exe`。首次启动会安装该版的独立词库，已有双卷词库和手动导入的数据保留在原安装中。

- **离线查词**：整合八部词典，支持日语假名、罗马字和常见活用，也支持英文短语和拼写建议。搜索框旁可速览释义，展开后可阅读词典原文和排版详情。
- **联想与反查**：输入词头前缀可看补全建议；想不起词时，可以输入中文描述，查找对应的日语或英语词。
- **文件与文件夹搜索**　通过 Everything 搜索本机文件和文件夹，结果旁的父目录可区分同名项。输入 `folder:项目` 可只查名称匹配“项目”的文件夹。选中文件夹按回车，或单击其名称，即可在资源管理器中进入该文件夹。按 Shift + Enter 可在上级目录中选中它。选中文件后按回车打开，QuickLook 运行时也可按空格预览。
- **GPT 自动跳转**：输入前缀“ai” + 空格输入问题(提示词)然后回车即可跳转至 ChatGPT 网页
- **计算器**：直接在搜索框输入算式，支持四则运算和科学函数。


# SearchBar_Edwin
Personal search tool for Windows. Press `Alt + Space` by default to look up a word in local dictionaries and search for files with the same query.

Windows x64 comes in [Chinese–English](https://github.com/EdwinJunyu/SearchBar_Edwin/releases/tag/v0.2.2-zh-en) and [Chinese–Japanese](https://github.com/EdwinJunyu/SearchBar_Edwin/releases/tag/v0.2.2-zh-ja) editions. The editions have the same features and bundle two Chinese–English or four Chinese–Japanese dictionary sources, respectively. Extract the ZIP and open `SearchBar_Edwin-win-x64/SearchBar_Edwin.exe` to install that edition's separate dictionary database. Existing combined and manually imported dictionaries remain in the original installation.

- **Offline dictionaries** — Search eight dictionaries using Japanese kana, romaji, or common inflected forms. English lookups support phrases and spelling suggestions. Read a short definition beside the search box or expand the entry to see the original text and formatting.
- **Suggestions and reverse lookup** — Type the start of a word for completions. If you can describe a word in Chinese but can’t recall it in Japanese or English, search by that description.
- **File and folder search** — Search local files and folders with Everything. Parent paths distinguish results with the same name. Type `folder:projects` to show only matching folders. Press Enter on a selected folder or click its name to open it in File Explorer. Shift + Enter selects it in its parent folder. For files, press Enter to open or Space to preview when QuickLook is running.
- **Jump to GPT** — Type the prefix "ai" followed by a space to enter the prompt, and then press Enter to jump to the ChatGPT webpage.
- **Calculator** — Enter an expression in the search box for arithmetic or scientific functions.
