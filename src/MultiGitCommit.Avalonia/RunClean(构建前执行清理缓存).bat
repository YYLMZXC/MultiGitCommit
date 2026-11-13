@echo off
setlocal enabledelayedexpansion

:: 删除所有 obj 和 bin 目录
for /d /r . %%d in (obj,bin) do @if exist "%%d" rd /s /q "%%d"

dotnet clean MultiGitCommit.slnx
:: 强制恢复项目
dotnet restore MultiGitCommit.slnx



pause