#!/usr/bin/env python3
r"""Gera o pacote de instalacao versionado do Marz Monitor.

Uso: python tools/build-package.py [--output-dir DIR]
1. Le a versao de Directory.Build.props (fonte unica, 0.1.x).
2. dotnet publish self-contained win-x64.
3. Copia Uninstall.ps1 para o publish e gera payload-manifest.json (SHA256 de cada arquivo).
4. Monta INSTALAR.cmd + Installer.ps1 (com versao e hash do manifesto embutidos) + scripts\Elevated.ps1 + LEIA-ME.
5. ZIP em <output-dir>\MonitorTray-<versao>-win-x64.zip (recusa sobrescrever) e imprime tamanho/SHA256.
"""
import argparse, hashlib, json, os, re, shutil, subprocess, tempfile, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get("DN") or "dotnet"

def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def version():
    m = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
    v = m.group(1)
    if not re.fullmatch(r"0\.1\.\d+", v): raise SystemExit(f"Versao fora da serie 0.1.x: {v}")
    return v

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--output-dir", default=str(Path.home() / "Downloads"))
    a = ap.parse_args()
    v = version()
    out = Path(a.output_dir) / f"MarzMonitor-{v}-win-x64.zip"
    if out.exists(): raise SystemExit(f"{out} ja existe: suba a versao em Directory.Build.props.")
    commit = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--short=7", "HEAD"], capture_output=True, text=True).stdout.strip() or "local"
    with tempfile.TemporaryDirectory(prefix="mt-pkg-") as tmp:
        tmp = Path(tmp); pub = tmp / "pkg" / "publish"
        r = subprocess.run([DOTNET, "publish", str(ROOT / "src/MonitorTray.App/MonitorTray.App.csproj"), "-c", "Release", "-r", "win-x64",
                            "--self-contained", "true", "-warnaserror", f"-p:SourceRevisionId={commit}", "-o", str(pub)], capture_output=True, text=True)
        if r.returncode != 0: print(r.stdout[-4000:]); raise SystemExit("publish falhou")
        for p in pub.rglob("*.pdb"): p.unlink()
        shutil.copyfile(ROOT / "installer/Uninstall.ps1", pub / "Uninstall.ps1")
        files = sorted(p for p in pub.rglob("*") if p.is_file())
        manifest = {"Version": v, "Commit": commit, "Files": [{"Name": p.relative_to(pub).as_posix(), "SHA256": sha(p), "Length": p.stat().st_size} for p in files]}
        pkg = tmp / "pkg"
        (pkg / "payload-manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
        mh = sha(pkg / "payload-manifest.json")
        inst = (ROOT / "installer/Installer.ps1").read_text(encoding="utf-8-sig")
        assert inst.count("__VERSION__") == 1 and inst.count("__MANIFEST_SHA256__") == 1
        (pkg / "Installer.ps1").write_text(inst.replace("__VERSION__", v).replace("__MANIFEST_SHA256__", mh), encoding="utf-8-sig", newline="\r\n")
        (pkg / "scripts").mkdir()
        shutil.copyfile(ROOT / "installer/Elevated.ps1", pkg / "scripts/Elevated.ps1")
        (pkg / "INSTALAR.cmd").write_bytes(('@echo off\r\ncd /d "%~dp0"\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Installer.ps1"\r\nexit /b %ERRORLEVEL%\r\n').encode("ascii"))
        shutil.copyfile(ROOT / "docs/INSTALL.md", pkg / "LEIA-ME.md")
        shutil.copyfile(ROOT / "CHANGELOG.md", pkg / "CHANGELOG.md")
        sums = "".join(f"{sha(p)}  {p.relative_to(pkg).as_posix()}\n" for p in sorted(pkg.rglob("*")) if p.is_file())
        (pkg / "SHA256SUMS.txt").write_text(sums, encoding="ascii")
        top = f"MarzMonitor-{v}"
        out.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(out, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for p in sorted(pkg.rglob("*")):
                if p.is_file(): z.write(p, f"{top}/{p.relative_to(pkg).as_posix()}")
        with zipfile.ZipFile(out) as z: assert z.testzip() is None
    print(json.dumps({"zip": str(out), "version": v, "commit": commit, "bytes": out.stat().st_size, "sha256": sha(out), "files": len(files)}))

if __name__ == "__main__":
    main()
