"""Assemble redistributable payloads and the matching source archive without local state."""
from pathlib import Path
import json, shutil, urllib.request, zipfile

root = Path(__file__).resolve().parent.parent
work = root / "artifacts/public-release"
licenses = root / "third_party/licenses"; licenses.mkdir(parents=True,exist_ok=True)
def download(url, path):
    if not path.exists():
        with urllib.request.urlopen(url,timeout=60) as response: path.write_bytes(response.read())
    return path.read_bytes()
shutil.copyfile(root/"ui/GxbrielZapret/LICENSE",licenses/"GXBRIEL_ZAPRET-MIT.txt")
shutil.copyfile(root.parent/"SOURCE AND REFERENCE/zapret2-v1.0.5.2/docs/LICENSE.txt",licenses/"zapret2-MIT.txt")
for font in ["hankengrotesk","spacegrotesk","jetbrainsmono"]:
    download(f"https://raw.githubusercontent.com/google/fonts/main/ofl/{font}/OFL.txt", licenses/f"{font}-OFL.txt")
for name,url in {
    "GPL-3.0.txt":"https://www.gnu.org/licenses/gpl-3.0.txt",
    "LGPL-3.0.txt":"https://www.gnu.org/licenses/lgpl-3.0.txt",
    "Cygwin-exceptions.txt":"https://cygwin.com/COPYING.LIB",
}.items(): download(url,licenses/name)
metadata=json.loads((work/"cygwin/packages.json").read_text())
shutil.copyfile(work/"cygwin/packages.json",root/"third_party/cygwin-packages.json")
cert=download("https://curl.se/ca/cacert.pem",work/"cacert.pem")
payload=root/"ui/GxbrielZapret/engine/blockcheck2-runtime.zip"
with zipfile.ZipFile(payload) as archive: scripts={n:archive.read(n) for n in archive.namelist() if not n.startswith("cygwin/")}
with zipfile.ZipFile(payload,"w",zipfile.ZIP_DEFLATED) as archive:
    for n,data in scripts.items(): archive.writestr(n,data)
    for path in sorted((work/"cygwin/root/bin").iterdir()):
        if path.suffix.lower() in [".exe",".dll"]: archive.write(path,"cygwin/bin/"+path.name)
    archive.write(work/"cygwin/root/bin/gawk.exe","cygwin/bin/awk.exe")
    archive.write(work/"cygwin/root/bin/curl.exe","cygwin/usr/local/bin/curl.exe")
    archive.writestr("cygwin/etc/pki/tls/certs/ca-bundle.crt",cert)
    for path in licenses.iterdir(): archive.write(path,"licenses/"+path.name)
sources=work/"third-party-sources.zip"
with zipfile.ZipFile(sources,"w",zipfile.ZIP_STORED) as archive:
    for record in metadata["files"]:
        if record["kind"]=="source": archive.write(work/"cygwin/cache"/record["path"],"cygwin/"+Path(record["path"]).name)
    for path in (work/"sources").iterdir(): archive.write(path,"engine/"+path.name)
    archive.write(work/"cygwin/packages.json","cygwin-packages.json")
    archive.write(work/"cygwin/setup.xz","cygwin-setup.xz")
    archive.write(work/"cygwin/setup.xz.sig","cygwin-setup.xz.sig")
    for path in licenses.iterdir(): archive.write(path,"licenses/"+path.name)
print("Redistributable runtime:",payload.stat().st_size,"bytes; corresponding sources:",sources.stat().st_size,"bytes")
