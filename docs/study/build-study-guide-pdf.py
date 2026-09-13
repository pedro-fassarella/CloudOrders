"""Small, reproducible Markdown-to-PDF builder for the CloudOrders study guide.

Usage:
    python docs/study/build-study-guide-pdf.py \
      docs/study/CloudOrders-Study-Guide.md \
      docs/study/CloudOrders-Study-Guide.pdf

The parser deliberately supports the Markdown constructs used by this guide:
headings, paragraphs, lists, fenced ASCII/code blocks, horizontal rules and blockquotes.
"""

from __future__ import annotations

import html
import re
import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.platypus import (
    KeepTogether,
    ListFlowable,
    ListItem,
    PageBreak,
    Paragraph,
    Preformatted,
    SimpleDocTemplate,
    Spacer,
)


PAGE_WIDTH, PAGE_HEIGHT = A4


def inline_markdown(value: str) -> str:
    """Translate the small safe subset of inline Markdown needed for this book."""
    value = html.escape(value)
    value = re.sub(r"`([^`]+)`", r'<font name="Courier">\1</font>', value)
    value = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", value)
    value = re.sub(r"\*([^*]+)\*", r"<i>\1</i>", value)
    value = re.sub(r"\[([^]]+)\]\([^)]+\)", r"\1", value)
    return value


def styles():
    base = getSampleStyleSheet()
    return {
        "title": ParagraphStyle(
            "BookTitle", parent=base["Title"], fontName="Helvetica-Bold", fontSize=28,
            leading=34, textColor=colors.HexColor("#123047"), alignment=TA_CENTER, spaceAfter=18,
        ),
        "subtitle": ParagraphStyle(
            "Subtitle", parent=base["Normal"], fontName="Helvetica", fontSize=13,
            leading=19, textColor=colors.HexColor("#415A6B"), alignment=TA_CENTER,
        ),
        "h1": ParagraphStyle(
            "H1", parent=base["Heading1"], fontName="Helvetica-Bold", fontSize=19,
            leading=24, textColor=colors.HexColor("#123047"), spaceBefore=20, spaceAfter=11,
            keepWithNext=True,
        ),
        "h2": ParagraphStyle(
            "H2", parent=base["Heading2"], fontName="Helvetica-Bold", fontSize=14,
            leading=18, textColor=colors.HexColor("#176B87"), spaceBefore=16, spaceAfter=7,
            keepWithNext=True,
        ),
        "h3": ParagraphStyle(
            "H3", parent=base["Heading3"], fontName="Helvetica-Bold", fontSize=11.5,
            leading=15, textColor=colors.HexColor("#204A5B"), spaceBefore=12, spaceAfter=5,
            keepWithNext=True,
        ),
        "body": ParagraphStyle(
            "Body", parent=base["BodyText"], fontName="Helvetica", fontSize=9.5,
            leading=14, spaceAfter=7, alignment=TA_LEFT,
        ),
        "quote": ParagraphStyle(
            "Quote", parent=base["BodyText"], fontName="Helvetica-Oblique", fontSize=9.5,
            leading=14, leftIndent=13, rightIndent=13, borderColor=colors.HexColor("#8CB9C8"),
            borderWidth=1, borderPadding=7, borderLeft=True, spaceAfter=10,
        ),
        "bullet": ParagraphStyle(
            "Bullet", parent=base["BodyText"], fontName="Helvetica", fontSize=9.3,
            leading=13, leftIndent=14, firstLineIndent=0, spaceAfter=3,
        ),
        "code": ParagraphStyle(
            "Code", fontName="Courier", fontSize=7.35, leading=9.1,
            textColor=colors.HexColor("#17212B"), backColor=colors.HexColor("#F1F5F7"),
            borderColor=colors.HexColor("#D4E0E5"), borderWidth=0.5, borderPadding=7,
            spaceBefore=3, spaceAfter=9,
        ),
    }


def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(colors.HexColor("#D4E0E5"))
    canvas.line(doc.leftMargin, 1.45 * cm, PAGE_WIDTH - doc.rightMargin, 1.45 * cm)
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(colors.HexColor("#526C7A"))
    canvas.drawString(doc.leftMargin, 1.0 * cm, "CloudOrders — Guia de Estudo de Arquitetura Distribuída")
    canvas.drawRightString(PAGE_WIDTH - doc.rightMargin, 1.0 * cm, str(doc.page))
    canvas.restoreState()


def title_page(book_styles):
    return [
        Spacer(1, 5.2 * cm),
        Paragraph("CloudOrders", book_styles["title"]),
        Paragraph("Guia de Estudo de Arquitetura Distribuída", book_styles["title"]),
        Spacer(1, 0.65 * cm),
        Paragraph(
            "Do walking skeleton à Transactional Outbox —\n"
            "o sistema implementado antes da fase de deployment em Azure",
            book_styles["subtitle"],
        ),
        Spacer(1, 1.35 * cm),
        Paragraph("Material de estudo baseado no repositório CloudOrders", book_styles["subtitle"]),
        PageBreak(),
    ]


def build_story(markdown: str, book_styles):
    lines = markdown.replace("\r\n", "\n").split("\n")
    story = title_page(book_styles)
    i = 0
    h1_count = 0
    paragraph_lines: list[str] = []

    def flush_paragraph():
        nonlocal paragraph_lines
        if paragraph_lines:
            story.append(Paragraph(inline_markdown(" ".join(paragraph_lines).strip()), book_styles["body"]))
            paragraph_lines = []

    while i < len(lines):
        line = lines[i]
        if line.startswith("```"):
            flush_paragraph()
            i += 1
            block = []
            while i < len(lines) and not lines[i].startswith("```"):
                block.append(lines[i])
                i += 1
            story.append(Preformatted("\n".join(block), book_styles["code"], maxLineLength=108))
        elif line.startswith("# "):
            flush_paragraph()
            h1_count += 1
            # The first H1 is represented by the designed title page. Every later
            # H1 is a book chapter and deserves a visible, unambiguous boundary.
            if h1_count > 1:
                story.append(PageBreak())
                story.append(Paragraph(inline_markdown(line[2:]), book_styles["h1"]))
        elif line.startswith("## "):
            flush_paragraph()
            story.append(Paragraph(inline_markdown(line[3:]), book_styles["h1"]))
        elif line.startswith("### "):
            flush_paragraph()
            story.append(Paragraph(inline_markdown(line[4:]), book_styles["h2"]))
        elif line.startswith("#### "):
            flush_paragraph()
            story.append(Paragraph(inline_markdown(line[5:]), book_styles["h3"]))
        elif line.strip() == "---":
            flush_paragraph()
            story.append(Spacer(1, 7))
        elif line.startswith("> "):
            flush_paragraph()
            story.append(Paragraph(inline_markdown(line[2:]), book_styles["quote"]))
        elif re.match(r"^[-*] ", line):
            flush_paragraph()
            items = []
            while i < len(lines) and re.match(r"^[-*] ", lines[i]):
                items.append(ListItem(Paragraph(inline_markdown(lines[i][2:]), book_styles["bullet"])))
                i += 1
            story.append(ListFlowable(items, bulletType="bullet", start="circle", leftIndent=18, spaceAfter=6))
            continue
        elif re.match(r"^\d+\. ", line):
            flush_paragraph()
            items = []
            while i < len(lines) and re.match(r"^\d+\. ", lines[i]):
                items.append(ListItem(Paragraph(inline_markdown(re.sub(r"^\d+\. ", "", lines[i])), book_styles["bullet"])))
                i += 1
            story.append(ListFlowable(items, bulletType="1", leftIndent=18, spaceAfter=6))
            continue
        elif not line.strip():
            flush_paragraph()
        else:
            paragraph_lines.append(line)
        i += 1
    flush_paragraph()
    return story


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        raise SystemExit(2)
    source, output = map(Path, sys.argv[1:])
    if not source.is_file():
        raise SystemExit(f"Markdown source not found: {source}")
    output.parent.mkdir(parents=True, exist_ok=True)
    doc = SimpleDocTemplate(
        str(output), pagesize=A4, leftMargin=1.75 * cm, rightMargin=1.75 * cm,
        topMargin=1.7 * cm, bottomMargin=1.9 * cm, title="CloudOrders — Guia de Estudo",
        author="CloudOrders",
    )
    doc.build(build_story(source.read_text(encoding="utf-8"), styles()), onFirstPage=footer, onLaterPages=footer)
    print(f"Generated {output}")


if __name__ == "__main__":
    main()
