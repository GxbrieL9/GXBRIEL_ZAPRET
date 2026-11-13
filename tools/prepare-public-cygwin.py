"""Download signed official Cygwin metadata, verified packages AND their corresponding sources.
No installer is run and no existing Cygwin installation is changed.
"""
import concurrent.futures, hashlib, io, json, lzma, os, re, subprocess, sys, tarfile, urllib.request
from pathlib import Path

root = Path(__file__).resolve().parent.parent
work = root / "artifacts/public-release/cygwin"
work.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(root / "artifacts/public-release/deps"))
mirror = "https://mirrors.kernel.org/sourceware/cygwin/"
def get(url, path):
    if not path.exists():
        req = urllib.request.Request(url, headers={"User-Agent":"GXBRIEL-ZAPRET-source-preparation"})
        with urllib.request.urlopen(req, timeout=90) as response: data = response.read()
        path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
    return path.read_bytes()

metadata = get(mirror + "x86_64/setup.xz", work / "setup.xz")
get(mirror + "x86_64/setup.xz.sig", work / "setup.xz.sig")
get("https://cygwin.com/key/pubring.asc", work / "cygwin-pubring.asc")
gpg = Path("C:/Program Files/Git/usr/bin/gpg.exe")
home = work / "gnupg"; home.mkdir(exist_ok=True)
common = [str(gpg), "--homedir", str(home), "--batch"]
subprocess.run(common + ["--import", str(work / "cygwin-pubring.asc")], check=True, capture_output=True)
subprocess.run(common + ["--verify", str(work / "setup.xz.sig"), str(work / "setup.xz")], check=True, capture_output=True)
text = lzma.decompress(metadata).decode("utf8")
packages, current, previous = {}, None, False
for line in text.splitlines():
    if line.startswith("@ "):
        current = {}; packages[line[2:]] = current; previous = False
    elif line.startswith("["): previous = True
    elif current is not None and not previous and ": " in line:
        key, value = line.split(": ", 1); current[key] = value
wanted = set("bash curl coreutils util-linux grep sed gawk findutils diffutils ipcalc procps-ng psmisc which openssl ca-certificates".split())
providers = {name: p for p, fields in packages.items() for name in re.sub(r"\([^)]*\)", "", fields.get("provides", "")).replace(",", " ").split()}
queue = list(wanted)
while queue:
    name = queue.pop()
    if name not in packages: raise RuntimeError("Unknown official package: " + name)
    dependencies = packages[name].get("requires", packages[name].get("depends2", ""))
    dependencies = re.sub(r"\([^)]*\)", "", dependencies).replace(",", " ").split()
    for dep in dependencies:
        if dep == "_windows": continue
        dep = providers.get(dep, dep) if dep not in packages else dep
        if dep not in wanted: wanted.add(dep); queue.append(dep)
records = {}
for name in sorted(wanted):
    p = packages[name]
    for kind in ["install", "source"]:
        if kind in p:
            path, length, digest = p[kind].split()[:3]
            records[path] = {"path":path, "size":int(length), "hash":digest, "kind":kind}
    if "install" in p and "source" not in p and "[source]" not in p:
        # Source-only packages may be named separately via external-source.
        source_name = p.get("external-source")
        if source_name and source_name in packages and "source" in packages[source_name]:
            path,length,digest=packages[source_name]["source"].split()[:3]
            records[path]={"path":path,"size":int(length),"hash":digest,"kind":"source"}
        else: raise RuntimeError("Missing corresponding source for " + name)
def download(record):
    path = work / "cache" / record["path"]
    if not path.exists(): print("Downloading",record["path"],flush=True)
    try: data = get(mirror + record["path"], path)
    except (TimeoutError, OSError): data = get("https://mirrors.dotsrc.org/cygwin/" + record["path"], path)
    algorithm = hashlib.sha512 if len(record["hash"]) == 128 else hashlib.sha256
    if len(data) != record["size"] or algorithm(data).hexdigest() != record["hash"]:
        raise RuntimeError("Package checksum mismatch: " + record["path"])
    return path
print("Signed metadata verified; downloading",len(records),"packages and sources",flush=True)
with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor: list(executor.map(download, records.values()))
target = work / "root"; target.mkdir(exist_ok=True)
for record in records.values():
    if record["kind"] != "install": continue
    path = work / "cache" / record["path"]
    data = path.read_bytes()
    if str(path).endswith(".zst"):
        import zstandard
        data = zstandard.ZstdDecompressor().stream_reader(io.BytesIO(data)).read()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as archive:
        for entry in archive:
            if not (entry.isfile() or entry.islnk()): continue
            name = entry.name.removeprefix("./")
            if name.startswith("usr/bin/"): name = name[4:]
            if any(character in name for character in '<>:"|?*'): continue
            destination = (target / name).resolve()
            if not destination.is_relative_to(target.resolve()): raise RuntimeError("Unsafe package path")
            stream = archive.extractfile(entry)
            if stream:
                destination.parent.mkdir(parents=True,exist_ok=True); destination.write_bytes(stream.read())
(work / "packages.json").write_text(json.dumps({"mirror":mirror,"packages":sorted(wanted),"files":list(records.values())},indent=2),encoding="utf8")
print("Cygwin binaries and matching sources prepared:",len(wanted),"packages",flush=True)
