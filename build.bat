@echo off
rem 控制台切到 UTF-8，保证下方中文提示不乱码（须放在最前）
chcp 65001 >nul 2>nul

rem ===========================================================================
rem  SimpleTrans  Release 构建脚本
rem
rem  作用：编译 4 个库的 Release 版本，并把 Unity 可直接使用的 DLL 收集到
rem        dist\SimpleTrans\ 目录。
rem  用法：双击运行，或在仓库根目录执行 build.bat
rem
rem  产物：dist\SimpleTrans\SimpleTrans.Core.dll
rem        dist\SimpleTrans\SimpleTrans.Protocol.dll
rem        dist\SimpleTrans\SimpleTrans.Server.dll
rem        dist\SimpleTrans\SimpleTrans.Client.dll
rem        （附同名 .pdb 便于在 Unity 中调试堆栈）
rem
rem  注意：本脚本内的变量统一加 ST_ 前缀，避免与 MSBuild 读取的环境变量
rem        同名（例如 OutDir / OutputPath / Configuration 等）而干扰编译输出。
rem ===========================================================================

setlocal
pushd "%~dp0"

set "ST_CONFIG=Release"
set "ST_TFM=netstandard2.1"
set "ST_OUTDIR=dist\SimpleTrans"

echo ============================================================
echo  SimpleTrans - 构建 Unity 可用的 Release DLL
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令。
    echo         请先安装 .NET SDK 8.0 或更高版本： https://dotnet.microsoft.com/download
    goto :fail
)

echo [1/3] 清理旧产物 ...
if exist "dist" rmdir /s /q "dist"
mkdir "%ST_OUTDIR%"
if not exist "%ST_OUTDIR%" goto :fail

echo.
echo [2/3] 编译 Release（netstandard2.1）...
rem 只需构建 Server 与 Client，二者的 ProjectReference 会连带编译 Core 与 Protocol。
dotnet build "src\SimpleTrans.Server\SimpleTrans.Server.csproj" -c %ST_CONFIG% --nologo -v minimal
if errorlevel 1 goto :fail
dotnet build "src\SimpleTrans.Client\SimpleTrans.Client.csproj" -c %ST_CONFIG% --nologo -v minimal
if errorlevel 1 goto :fail

echo.
echo [3/3] 收集产物到 %ST_OUTDIR% ...
set "ST_DIR_CORE=src\SimpleTrans.Core\bin\%ST_CONFIG%\%ST_TFM%"
set "ST_DIR_PROTO=src\SimpleTrans.Protocol\bin\%ST_CONFIG%\%ST_TFM%"
set "ST_DIR_SERVER=src\SimpleTrans.Server\bin\%ST_CONFIG%\%ST_TFM%"
set "ST_DIR_CLIENT=src\SimpleTrans.Client\bin\%ST_CONFIG%\%ST_TFM%"

call :copy_one "SimpleTrans.Core"     "%ST_DIR_CORE%"
if errorlevel 1 goto :fail
call :copy_one "SimpleTrans.Protocol" "%ST_DIR_PROTO%"
if errorlevel 1 goto :fail
call :copy_one "SimpleTrans.Server"   "%ST_DIR_SERVER%"
if errorlevel 1 goto :fail
call :copy_one "SimpleTrans.Client"   "%ST_DIR_CLIENT%"
if errorlevel 1 goto :fail

rem pdb 为可选，缺失不报错（便于在 Unity 中看到可读堆栈）
copy /y "%ST_DIR_CORE%\SimpleTrans.Core.pdb"     "%ST_OUTDIR%" >nul 2>nul
copy /y "%ST_DIR_PROTO%\SimpleTrans.Protocol.pdb" "%ST_OUTDIR%" >nul 2>nul
copy /y "%ST_DIR_SERVER%\SimpleTrans.Server.pdb"  "%ST_OUTDIR%" >nul 2>nul
copy /y "%ST_DIR_CLIENT%\SimpleTrans.Client.pdb"  "%ST_OUTDIR%" >nul 2>nul

rem IL2CPP 兜底：本库不使用反射，通常无需本文件，保留可避免 Medium/High 剥离误伤
if exist "link.xml" copy /y "link.xml" "%ST_OUTDIR%" >nul

echo.
echo ---- 产物清单 ----
dir /b "%ST_OUTDIR%"
echo.

echo ============================================================
echo  构建完成
echo  产物目录： %CD%\%ST_OUTDIR%
echo.
echo  使用方法：把 %ST_OUTDIR% 文件夹整体复制到 Unity 工程的
echo            Assets\Plugins\ 目录下即可（Unity 会自动导入）。
echo.
echo  注意：
echo    1) Player Settings 中 Api Compatibility Level 需为 .NET Standard 2.1
echo       （Unity 2021.3 及以上版本默认为此项）。
echo    2) 若已通过 UPM 源码包引入 SimpleTrans，请勿再放入这些 DLL，
echo       否则会出现程序集同名冲突。
echo    3) 不要在 UI / Unity 主线程上对返回的 Task 使用 .Result / .Wait()，
echo       需要回调到主线程请设置 ClientOptions.CallbackContext。
echo ============================================================

popd
endlocal
exit /b 0

rem ---------------------------------------------------------------------------
rem  从指定目录复制 <名称>.dll 到产物目录；找不到则报错退出。
rem  %1 = 程序集名   %2 = 源目录
rem ---------------------------------------------------------------------------
:copy_one
if not exist "%~2\%~1.dll" (
    echo [错误] 未找到编译产物： %~2\%~1.dll
    exit /b 1
)
copy /y "%~2\%~1.dll" "%ST_OUTDIR%" >nul
exit /b %errorlevel%

:fail
echo.
echo [失败] 构建未完成，请查看上方错误信息。
popd
endlocal
exit /b 1
