"""Run standalone queue regressions using the scripting tools bundled with Unity.
Usage: python3 Tests~/run_regressions.py /Applications/Unity/Hub/Editor/<version>/Unity.app/Contents
"""
import pathlib
import subprocess
import sys
import tempfile

contents = pathlib.Path(sys.argv[1]).resolve()
scripting = contents / 'Resources/Scripting'
compiler = sorted((scripting / 'DotNetSdk/sdk').glob('*/Roslyn/bincore/csc.dll'))[-1]
references = scripting / 'MonoBleedingEdge/lib/mono/4.8-api'
root = pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='addressing-regression-') as temporary:
    output = pathlib.Path(temporary) / 'regressions.exe'
    subprocess.run([
        str(scripting / 'NetCoreRuntime/dotnet'), str(compiler),
        '-nologo', '-target:exe', '-langversion:9.0', '-nostdlib+', f'-out:{output}',
        *[f'-r:{references / name}' for name in (
            'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll')],
        str(root / 'Tests~/AutoAddressingPostprocessorRegression.cs'),
        str(root / 'Editor/AutoAddressing/AutoAddressingPostprocessor.cs'),
    ], check=True)
    subprocess.run([str(scripting / 'MonoBleedingEdge/bin/mono'), str(output)], check=True)
