@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Genera los documentos de todos los proyectos de ejemplo en esta carpeta.
rem Uso: doble clic o ejecutar desde CMD:
rem   generar-ejemplos.bat

set "ROOT=%~dp0"
cd /d "%ROOT%"

set /a OK=0
set /a FAIL=0

for /d %%D in (*) do (
    if exist "%%D\*.csproj" (
        echo.
        echo === %%D ===
        pushd "%%D"
        dotnet run
        if errorlevel 1 (
            echo FAILED: %%D
            set /a FAIL+=1
        ) else (
            set /a OK+=1
        )
        popd
    )
)

echo.
echo ==============================
echo Ejemplos correctos: !OK!
echo Ejemplos fallidos:  !FAIL!
echo ==============================

if !FAIL! GTR 0 exit /b 1
exit /b 0
