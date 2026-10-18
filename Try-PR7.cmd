@echo off
setlocal
set "PR7_SOURCE=%~dp0.review\codex-pr7-2026-10-17\preview-source"
set "PR7_DATA=%~dp0.review\codex-pr7-2026-10-17\preview"
set "PR7_NODE=C:\Users\benit\AppData\Local\Author Software\nvm\installs\v22.23.3\node.exe"
if not exist "%PR7_SOURCE%\planwharf.cjs" goto missing
if exist "%PR7_NODE%" goto launch
where node >nul 2>nul
if errorlevel 1 goto missingnode
set "PR7_NODE=node"
:launch
echo Starting PR 7 with the inspected assignment correction.
echo This preview uses a separate test notebook.
echo If no window opens, visit http://127.0.0.1:4177 in your browser.
"%PR7_NODE%" "%PR7_SOURCE%\planwharf.cjs" --plan "%PR7_DATA%\notebook.json" --attempts-dir "%PR7_DATA%\attempts" --port 4177
if errorlevel 1 pause
exit /b
:missing
echo The preview source folder is missing: %PR7_SOURCE%
pause
exit /b 1
:missingnode
echo Node.js is unavailable. Install Node.js or restore the configured runtime.
pause
exit /b 1
