#!/usr/bin/env python3
"""Extrai do resumo do `fmp capture` os números de latência, para comparar rodadas.

Uso: python3 extrair.py <saída do fmp>... > tabela.csv
Cada arquivo é a saída completa de um `fmp capture` (o botão "copiar a saída" do HypeFrame serve).
Colunas em ms; vazio quando a rodada não tem o dado (ex.: offscreen não tem antecedência).
"""
import csv
import re
import sys

NUM = r"([0-9]+(?:\.[0-9]+)?)"


def find(pattern, text, group=1):
    m = re.search(pattern, text, re.M)
    return m.group(group) if m else ""


def parse(path):
    t = open(path, encoding="utf-8", errors="replace").read()
    row = {"arquivo": path.rsplit("/", 1)[-1]}
    row["modo"] = find(r"negotiated\s+(\S+ \S+ @ [0-9.]+ fps)", t)
    row["swapchain"] = find(r"swapchain (\S+ / \S+)", t)
    row["relogio"] = ("vrr" if re.search(r"^\s*clock\s+VARIABLE", t, re.M)
                      else "fixo" if re.search(r"^\s*clock\s+fixed", t, re.M) else "")
    row["offscreen"] = "sim" if re.search(r"^\s*display\s+offscreen", t, re.M) else "não"
    row["fonte_T"] = find(r"source interval T " + NUM, t)
    row["captura_a_tela_p50"] = find(r"latency\s+a real frame is shown " + NUM, t)
    row["captura_a_tela_p99"] = find(r"latency\s+a real frame is shown [0-9.]+ ms after its capture timestamp \(p99 " + NUM, t)
    row["chegada"] = find(r"arrival lag " + NUM, t)
    row["usb"] = find(r"\(USB delivery\) p50 " + NUM, t)
    row["copia_gpu"] = find(r"capture gpu upload p50 " + NUM, t)
    row["geracao_total"] = find(r"the generation's " + NUM, t)
    row["hold"] = find(r"hold " + NUM + r" \+ lead", t)
    row["lead_geracao"] = find(r"hold [0-9.]+ \+ lead " + NUM, t)
    row["alinhamento"] = find(r"alignment " + NUM + r"\)", t)
    row["antecedencia"] = find(r"present lead " + NUM + r" \+ the stride", t)
    row["arredondamento"] = find(r"the stride's rounding \(p50 " + NUM, t)
    row["tela_apos_entrega"] = find(r"measured " + NUM + r", p50 of every present's hand-over", t)
    leads = re.search(r"^\s*lead\s+changes: (.*)$", t, re.M)
    row["antecedencia_mudancas"] = leads.group(1).strip() if leads else ""
    row["d_p50"] = find(r"generation GPU time p50 " + NUM, t)
    row["d_p99"] = find(r"generation GPU time p50 [0-9.]+ ms, p99 " + NUM, t)
    row["gerados"] = find(r"generation GPU time p50 [0-9.]+ ms, p99 [0-9.]+ ms, max [0-9.]+ ms over ([0-9]+)", t)
    row["slots"] = find(r"slots\s+([0-9]+) measured", t)
    row["perdidos"] = find(r"missed\s+([0-9]+) slot", t)
    verdicts = re.findall(r"^\s*verdict\s+(PASS|FAIL)\b", t, re.M)  # o do capture é o último (bench present vem antes)
    row["veredito"] = verdicts[-1] if verdicts else ""
    return row


def main():
    rows = [parse(p) for p in sys.argv[1:]]
    if not rows:
        print(__doc__)
        return 2
    w = csv.DictWriter(sys.stdout, fieldnames=list(rows[0].keys()))
    w.writeheader()
    w.writerows(rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
