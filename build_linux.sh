#!/bin/bash
# Release build on Linux: Roslyn (C# 5) against .NET Framework 4.8 reference assemblies
set -e
cd "$(dirname "$0")"
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
R=/usr/lib/mono/4.8-api
mkdir -p build
dotnet $CSC -nologo -noconfig -nostdlib -langversion:5 -optimize+ -target:winexe -out:build/EasyLib.exe \
  -win32icon:src/Assets/EasyLib.ico -resource:src/Assets/EasyLib.ico,EasyLib.Assets.EasyLib.ico -resource:src/Assets/logo-mark.png,EasyLib.Assets.logo-mark.png \
  -r:$R/mscorlib.dll -r:$R/System.dll -r:$R/System.Core.dll -r:$R/System.Windows.Forms.dll -r:$R/System.Drawing.dll -r:$R/System.Web.Extensions.dll -r:$R/System.IO.Compression.dll -r:$R/System.IO.Compression.FileSystem.dll src/*.cs
