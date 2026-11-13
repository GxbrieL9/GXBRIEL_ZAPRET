"""Create an allow-listed, history-free GitHub working copy, never publish local runtime data."""
from pathlib import Path
import hashlib, json, os, re, shutil, subprocess, sys

root = Path(__file__).resolve().parent.parent
destination = Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/"artifacts/github-public"
if destination == root or root.is_relative_to(destination): raise SystemExit("Недопустимый каталог публичной копии")
destination.mkdir(parents=True,exist_ok=True)
def copy(path):
    relative = path.relative_to(root)
    target = destination/relative; target.parent.mkdir(parents=True,exist_ok=True); shutil.copyfile(path,target)
for relative in ["README.md","LICENSE","THIRD_PARTY.md",".github/workflows/release.yml","docs/ПУБЛИКАЦИЯ.md","docs/ИЗМЕНЕНИЯ.md",
                 "tools/new-public-release.ps1","tools/prepare-public-cygwin.py","tools/package-public-payloads.py","tools/export-public-repository.py"]:
    copy(root/relative)
allowed = {"Assets","Controls","Harness","Localization","Models","Mvvm","Services","Startup","Themes","ViewModels","Views","tests","engine","blockcheck2"}
extensions = {".cs",".xaml",".csproj",".sln",".json",".ttf",".png",".ico",".pem",".txt",".bat",".ps1",".sh",".lua",".md",".zip",".manifest",".config"}
ui=root/"ui/GxbrielZapret"
for path in ui.rglob("*"):
    if not path.is_file() or path.is_symlink(): continue
    relative=path.relative_to(ui)
    if any(part in {".git",".serena",".vscode","bin","obj","TestResults","__pycache__"} for part in relative.parts): continue
    if len(relative.parts)>1 and relative.parts[0] not in allowed: continue
    if path.suffix.lower() not in extensions and path.name!="LICENSE": continue
    if path.name in {"zapret2_runtime.zip","README.md","README.en.md"}: continue
    if path.suffix.lower()==".pem" and path.name!="release-public-key.pem": raise RuntimeError("Неизвестный ключ в исходниках")
    copy(path)
for path in (root/"third_party").rglob("*"):
    if path.is_file():copy(path)
# Embed only the committed baseline lists; personal custom lists always start empty.
lists=destination/"profiles/lists";lists.mkdir(parents=True,exist_ok=True)
paths=subprocess.check_output(["git","ls-tree","-r","--name-only","HEAD","profiles/lists"],cwd=root,text=True).splitlines()
for relative in paths:
    if not relative.endswith('.txt') or relative.count('/')!=2:continue
    data=subprocess.check_output(["git","show","HEAD:"+relative],cwd=root)
    (destination/relative).write_bytes(data)
for name in ['list-user.txt','list-exclude-user.txt']:(lists/name).write_text('# Пользовательский список. По умолчанию пуст.\n',encoding='utf8')
(destination/'.gitignore').write_text('**/bin/\n**/obj/\nartifacts/\n.vs/\n.vscode/\n.serena/\n*.user\n*.log\n*.tmp\n*.previous-*\n*-private.pem\n.env*\n',encoding='utf8')
inventory=[]
for path in destination.rglob('*'):
    if not path.is_file() or '.git' in path.relative_to(destination).parts:continue
    relative=path.relative_to(destination).as_posix();data=path.read_bytes()
    if path.suffix.lower() in {'.cs','.json','.md','.ps1','.py','.yml','.txt','.pem'}:
        text=data.decode('utf8',errors='replace')
        if re.search(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}',text):raise RuntimeError('Обнаружен секрет: '+relative)
        username = os.environ.get('USERNAME','')
        if username and re.search(r'C:[\\/]+Users[\\/]+' + re.escape(username) + r'\b',text,re.I):raise RuntimeError('Обнаружены личные данные: '+relative)
    if len(data)>90*1024*1024:raise RuntimeError('Слишком большой файл исходного репозитория: '+relative)
    inventory.append({'path':relative,'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)})
(root/'artifacts/public-release/publication-manifest.json').write_text(json.dumps(inventory,ensure_ascii=False,indent=2),encoding='utf8')
print('Публичная копия подготовлена:',len(inventory),'файлов;',sum(x['bytes'] for x in inventory),'байт')
