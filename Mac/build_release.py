#!/usr/bin/env python3
"""Build or sign/notarize one native Mac release in a fresh external workspace.

This command never publishes, changes installed apps, or accesses floppy hardware.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import stat
import subprocess
import sys
import zipfile


def digest(file):
    h = hashlib.sha256()
    with file.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def inventory(app):
    result = []
    for file in sorted(app.rglob('*')):
        if file.is_symlink():
            raise RuntimeError('Unexpected linked bundle member: ' + str(file))
        if file.is_file():
            result.append(dict(path=file.relative_to(app).as_posix(), bytes=file.stat().st_size,
                               sha256=digest(file), executable=bool(file.stat().st_mode & 0o111)))
    return result


def runtime_notices(out, macos):
    """Keep required package notices in one compact document outside the app."""
    deps=json.loads((macos/'HamsterWeazle.deps.json').read_text())
    packages=set(deps['libraries'])
    packages.update('Microsoft.NETCore.App.Runtime.osx-x64/'+p.name for p in (out/'nuget/microsoft.netcore.app.runtime.osx-x64').glob('*') if p.is_dir())
    packages.update('Microsoft.NETCore.App.Runtime.osx-arm64/'+p.name for p in (out/'nuget/microsoft.netcore.app.runtime.osx-arm64').glob('*') if p.is_dir())
    records=[]
    parts=['Third-party runtime licenses and notices\n']
    for package in sorted(packages):
        directory=out/'nuget'/package.lower()
        if not directory.is_dir():
            continue
        for file in sorted(directory.iterdir()):
            name=file.name.upper()
            if file.is_file() and (name.startswith(('LICENSE','LICENCE','COPYING','NOTICE','THIRD-PARTY-NOTICES'))):
                text=file.read_text(encoding='utf-8-sig',errors='strict')
                parts.append('\n'+'='*72+'\n'+package+' / '+file.name+'\n'+'='*72+'\n'+text+'\n')
                records.append(dict(package=package,name=file.name,bytes=file.stat().st_size,sha256=digest(file)))
    assert any('microsoft.netcore.app.runtime' in r['package'].lower() for r in records)
    target=out/'ThirdPartyNotices.txt'
    target.write_text('\n'.join(parts),encoding='utf-8')
    return dict(path=str(target),bytes=target.stat().st_size,sha256=digest(target),inputs=records)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase', choices=['build', 'package'])
    p.add_argument('--rid', choices=['osx-x64', 'osx-arm64'], required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--sign-identity')
    p.add_argument('--notary-profile')
    p.add_argument('--resume-publish', action='store_true', help='Finish the envelope after a completed, inventoried publish')
    args = p.parse_args()
    assert sys.platform == 'darwin', 'Run native release work on macOS'
    root = Path(__file__).resolve().parent.parent
    out = args.out.resolve()
    external = Path('/Volumes/External/ChatGPT').resolve()
    assert out.is_relative_to(external) and out != external, 'Use a task folder on External/ChatGPT'
    assert not out.is_relative_to(root) and not root.is_relative_to(out), 'Separate inputs from output'
    assert args.dotnet.is_file()
    version = re.search(r'<Version>([^<]+)</Version>', (root/'Mac/HamsterWeazle.csproj').read_text())[1]
    assert re.fullmatch(r'\d+\.\d+\.\d+', version)
    if args.phase == 'build':
        if args.resume_publish:
            completed=json.loads((out/'completed-publish.json').read_text())
            assert completed['passed'] and completed['rid']==args.rid
            assert completed['sourceManifestSha256']==digest(root/'PRIVATE-SOURCE.json')
            assert completed['publishedFiles']==inventory(out/'HamsterWeazle.app')
            for row in json.loads((root/'PRIVATE-SOURCE.json').read_text())['files']:
                if row['path']!='Mac/build_release.py':
                    assert digest(root/row['path'])==row['sha256'], 'Compiled source changed after publish'
        else:
            assert not out.exists(), 'Preserve previous qualified or incomplete output'
            assert shutil.disk_usage(external).free > 1024**3, 'Free at least 1 GiB on External first'
            out.mkdir(parents=True)
    else:
        assert out.is_dir() and args.sign_identity and args.notary_profile
    for name in ['tmp', 'dotnet-home', 'nuget']:
        (out/name).mkdir(exist_ok=True)
    env = {**os.environ, 'TEMP':str(out/'tmp'), 'TMP':str(out/'tmp'), 'TMPDIR':str(out/'tmp')+'/',
           'DOTNET_ROOT':str(args.dotnet.parent), 'DOTNET_ROOT_X64':str(args.dotnet.parent),
           'DOTNET_CLI_HOME':str(out/'dotnet-home'), 'NUGET_PACKAGES':str(out/'nuget'),
           'DOTNET_NOLOGO':'1', 'DOTNET_CLI_TELEMETRY_OPTOUT':'1'}
    commands = []
    def run(argv, log):
        with (out/log).open('w') as f:
            r = subprocess.run([str(x) for x in argv], cwd=root/'Mac', env=env,
                               stdout=f, stderr=subprocess.STDOUT)
        commands.append(dict(command=[str(x) for x in argv], log=log, exitCode=r.returncode))
        assert r.returncode == 0, 'Failed command; see ' + str(out/log)
        return (out/log).read_text()
    app = out/'HamsterWeazle.app'
    macos = app/'Contents/MacOS'
    resources = app/'Contents/Resources'
    if args.phase == 'build':
        if not args.resume_publish:
            macos.mkdir(parents=True)
            resources.mkdir()
            run([args.dotnet, 'publish', root/'Mac/HamsterWeazle.csproj', '-c', 'Release', '-r', args.rid,
                 '--self-contained', 'true', '--artifacts-path', out/'dotnet-artifacts',
                 '-p:PublishSingleFile=false', '-p:UseAppHost=true', '-o', macos], 'publish.log')
            (out/'completed-publish.json').write_text(json.dumps(dict(passed=True,rid=args.rid,
                sourceManifestSha256=digest(root/'PRIVATE-SOURCE.json'),publishedFiles=inventory(app)),indent=2)+'\n')
        assert (macos/'HamsterWeazle').is_file() and (macos/'HamsterWeazle.dll').is_file()
        expected_arch = 'x86_64' if args.rid == 'osx-x64' else 'arm64'
        for name in ['HamsterWeazle','libcoreclr.dylib','libAvaloniaNative.dylib']:
            arches=run(['/usr/bin/lipo','-archs',macos/name], 'architecture-'+name+'.log').split()
            assert expected_arch in arches
            if name!='libAvaloniaNative.dylib':
                assert arches==[expected_arch]
        (macos/'HamsterWeazle').chmod(0o755)
        with (app/'Contents/Info.plist').open('wb') as f:
            plistlib.dump(dict(CFBundleIdentifier='com.meanhamster.hamsterweazle',CFBundleName='HamsterWeazle',
                CFBundleDisplayName='HamsterWeazle',CFBundleExecutable='HamsterWeazle',CFBundleIconFile='AppIcon',
                CFBundleVersion=version,CFBundleShortVersionString=version,CFBundlePackageType='APPL',
                NSHighResolutionCapable=True,LSMinimumSystemVersion='14.0'),f)
        iconset = out/'tmp/AppIcon.iconset'
        iconset.mkdir()
        png = out/'tmp/icon.png'
        run(['/usr/bin/sips','-s','format','png',root/'Mac/Assets/icon.ico','--out',png],'icon-source.log')
        for size in [16,32,128,256,512]:
            for scale, suffix in [(1,''),(2,'@2x')]:
                run(['/usr/bin/sips','-z',str(size*scale),str(size*scale),png,
                     '--out',iconset/('icon_'+str(size)+'x'+str(size)+suffix+'.png')],
                    'icon-'+str(size)+suffix+'.log')
        run(['/usr/bin/iconutil','-c','icns',iconset,'-o',resources/'AppIcon.icns'], 'iconutil.log')
        shutil.copyfile(root/'LICENSE', resources/'LICENSE.txt')
        shutil.copyfile(root/'Mac/Assets/Fonts/DSEG-LICENSE.txt', resources/'DSEG-LICENSE.txt')
        receipt=dict(passed=True,phase='build',version=version,rid=args.rid,app=str(app),
                     sourceInventory=json.loads((root/'PRIVATE-SOURCE.json').read_text()),
                     envelopeBuilderSha256=digest(Path(__file__)),reusedCompletedPublish=args.resume_publish,
                     bundleFiles=inventory(app),commands=commands,published=False,physicalHardwareTested=False)
        (out/'build-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    else:
        previous=json.loads((out/'build-receipt.json').read_text())
        assert previous['passed'] and previous['rid']==args.rid and previous['version']==version
        assert inventory(app)==previous['bundleFiles'], 'Completed application changed before signing'
        qualification=json.loads((out/'native-checks.json').read_text())
        assert qualification['passed'] and qualification['version']==version and qualification['rid']==args.rid
        assert qualification['appAssemblySha256']==digest(macos/'HamsterWeazle.dll')
        entitlements=out/'entitlements.plist'
        with entitlements.open('wb') as f:
            plistlib.dump({'com.apple.security.cs.allow-jit':True,
                          'com.apple.security.cs.allow-unsigned-executable-memory':True,
                          'com.apple.security.cs.disable-library-validation':True},f)
        # Sign dependencies before the app host: codesign treats the host as the
        # enclosing bundle and checks its nested code immediately.
        for file in sorted(macos.iterdir(), key=lambda f:(f.name=='HamsterWeazle',f.name)):
            if file.is_file():
                run(['/usr/bin/codesign','--force','--options','runtime','--timestamp','--entitlements',
                     entitlements,'--sign',args.sign_identity,file], 'sign-'+file.name+'.log')
        run(['/usr/bin/codesign','--force','--options','runtime','--timestamp','--entitlements',
             entitlements,'--sign',args.sign_identity,app], 'sign-app.log')
        run(['/usr/bin/codesign','--verify','--deep','--strict',app],'signature.log')
        notary=out/'notary-input.zip'
        run(['/usr/bin/ditto','-c','-k','--sequesterRsrc','--keepParent',app,notary],'notary-archive.log')
        result=json.loads(run(['/usr/bin/xcrun','notarytool','submit',notary,'--keychain-profile',
                              args.notary_profile,'--wait','--output-format','json'],'notary-result.json'))
        assert result['status']=='Accepted'
        run(['/usr/bin/xcrun','stapler','staple',app],'staple.log')
        run(['/usr/bin/xcrun','stapler','validate',app],'staple-validate.log')
        run(['/usr/sbin/spctl','--assess','--type','execute','--verbose=2',app],'gatekeeper.log')
        label='Intel' if args.rid=='osx-x64' else 'AppleSilicon'
        archive=out/('HamsterWeazle-Mac-'+label+'.zip')
        assert not archive.exists()
        run(['/usr/bin/ditto','-c','-k','--sequesterRsrc','--keepParent',app,archive],'archive.log')
        notices=runtime_notices(out,macos)
        with zipfile.ZipFile(archive,'a',compression=zipfile.ZIP_DEFLATED) as z:
            z.write(out/'ThirdPartyNotices.txt','ThirdPartyNotices.txt')
        roundtrip=out/'archive-roundtrip'
        run(['/usr/bin/ditto','-x','-k',archive,roundtrip],'extract.log')
        assert inventory(roundtrip/app.name)==inventory(app), 'Archive must preserve all bytes and executable modes'
        run(['/usr/bin/codesign','--verify','--deep','--strict',roundtrip/app.name],'roundtrip-signature.log')
        run(['/usr/sbin/spctl','--assess','--type','execute',roundtrip/app.name],'roundtrip-gatekeeper.log')
        receipt=dict(passed=True,version=version,rid=args.rid,app=str(app),
            artifact=dict(path=str(archive),bytes=archive.stat().st_size,sha256=digest(archive)),
            signatureVerified=True,notarized=True,notarization=result,archiveRoundtripVerified=True,
            sourceCodeBundlesIncluded=False,commercialOriginalsIncluded=False,physicalIntelAcceptance=False,
            nativeChecks=qualification,bundleFiles=inventory(app),envelopeBuilderSha256=digest(Path(__file__)),commands=commands,published=False)
        receipt['runtimeNotices']=notices
        (out/'release-qualification.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps(dict(passed=True,phase=args.phase,version=version,rid=args.rid,output=str(out))),flush=True)


if __name__=='__main__':
    main()
