@echo off
setlocal
chcp 65001 >nul
set "book_root=%~dp0"
set "book_app=%book_root%app\SearchBar_Edwin.exe"
if not exist "%book_app%" set "book_app=%book_root%app\E_Search.exe"
set "E_SEARCH_DB=%book_root%index\dictionaries.db"
if not exist "%book_app%" goto missing
if not exist "%book_root%books\E_search-merge.json" goto missing
echo 1. 中英词书
echo 2. 日语词书
echo 3. 同时查两卷
set "book_group="
choice /c 123 /n /m "请选择 1、2 或 3 "
if errorlevel 3 goto chosen
if errorlevel 2 (set "book_group=zh-ja") else (set "book_group=zh-en")
:chosen
if not exist "%book_root%index" mkdir "%book_root%index"
if exist "%book_root%index\import-complete.json" if exist "%E_SEARCH_DB%" (
    fc /b "%book_root%index\import-complete.json" "%book_root%books\E_search-merge.json" >nul
    if not errorlevel 1 goto show
)
echo 正在从两卷生成独立检索库，首次运行需要数分钟。
start "" /wait "%book_app%" import-merged "%book_root%books"
if errorlevel 1 goto failed
copy /b /y "%book_root%books\E_search-merge.json" "%book_root%index\import-complete.json" >nul
:show
start "" "%book_app%" gui-merged "%E_SEARCH_DB%" %book_group%
exit /b 0
:missing
echo 无法找到 app 中的程序或 books 中的双卷清单，请在完整交付目录中运行。
pause
exit /b 1
:failed
echo 导入失败。请检查 books 中的词典和剩余磁盘空间。
pause
exit /b 1
