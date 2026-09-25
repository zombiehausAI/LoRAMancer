import pathlib

chroma_file = pathlib.Path(r"C:\AI\LoRAMancer\tools\ai-toolkit\extensions_built_in\diffusion_models\chroma\chroma_model.py")

if not chroma_file.exists():
    print(f"File not found: {chroma_file}")
    exit(1)

content = chroma_file.read_text(encoding="utf-8")
if "# [loramancer] chroma-prompt-guard" in content:
    print("chroma_model.py already contains patch.")
    exit(0)

target = "text_inputs = self.tokenizer[1]("
if target not in content:
    print(f"Could not find target line: {target}")
    exit(1)

replacement = """# [loramancer] chroma-prompt-guard
        if prompt is None:
            prompt = ""
        elif isinstance(prompt, list):
            prompt = [p if p is not None else "" for p in prompt]
        text_inputs = self.tokenizer[1]("""

content = content.replace(target, replacement, 1)
chroma_file.write_text(content, encoding="utf-8")
print("SUCCESS: Successfully patched chroma_model.py!")
