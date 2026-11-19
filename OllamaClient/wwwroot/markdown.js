"use strict";

const markdownOutput = (() => {
  const inlineTags = { paragraph: "p", strong: "strong", em: "em", del: "del", blockquote: "blockquote" };
  const languageNames = {
    csharp: "C#", javascript: "JavaScript", typescript: "TypeScript", json: "JSON",
    sql: "SQL", python: "Python", bash: "Bash", markup: "HTML / XML", css: "CSS"
  };
  const languageAliases = {
    "c#": "csharp", cs: "csharp", js: "javascript", ts: "typescript",
    py: "python", sh: "bash", shell: "bash", html: "markup", xml: "markup"
  };
  const entityDecoder = document.createElement("textarea");

  function decodeEntities(text) {
    return text.replace(/&(?:#(?:[0-9]+|[xX][0-9a-fA-F]+)|[a-zA-Z][a-zA-Z0-9]+);/g, entity => {
      entityDecoder.innerHTML = entity;
      return entityDecoder.value;
    });
  }

  function element(tag, text = "", className = "") {
    const node = document.createElement(tag);
    node.textContent = text;
    if (className)
      node.className = className;
    return node;
  }

  function link(token, content) {
    try {
      const url = new URL(decodeEntities(token.href));
      if (url.protocol === "https:" || url.protocol === "http:" || url.protocol === "mailto:") {
        const anchor = document.createElement("a");
        anchor.href = url.href;
        anchor.target = "_blank";
        anchor.rel = "noopener noreferrer";
        if (token.title)
          anchor.title = decodeEntities(token.title);
        anchor.append(content);
        return anchor;
      }
    } catch {
      // A relative or malformed address has no useful destination in a model response.
    }
    return content;
  }

  function resolveLanguage(language) {
    const name = language.toLowerCase();
    return Object.hasOwn(languageAliases, name) ? languageAliases[name] : name;
  }

  function appendCodeTokens(parent, tokens) {
    for (const token of tokens) {
      if (typeof token === "string") {
        parent.append(token);
      } else {
        const span = element("span", "", "code-token " + token.type);
        appendCodeTokens(span, Array.isArray(token.content) ? token.content : [token.content]);
        parent.append(span);
      }
    }
  }

  function renderCode(target, source, language) {
    const id = resolveLanguage(language);
    const prism = window.Prism;
    if (prism && Object.hasOwn(languageNames, id) && prism.languages[id]) {
      try {
        const tokens = prism.tokenize(source, prism.languages[id]);
        const fragment = document.createDocumentFragment();
        appendCodeTokens(fragment, tokens);
        target.replaceChildren(fragment);
        return;
      } catch {
        // Keep partial or unsupported code readable if tokenization fails.
      }
    }
    target.textContent = source;
  }

  function codeBlock(token, showCopy) {
    const block = element("div", "", "code-block");
    const toolbar = element("div", "", "code-toolbar");
    const language = token.lang?.trim().split(/\s+/, 1)[0] || "csharp";
    const languageId = resolveLanguage(language);
    const languageLabel = languageId === "markup" && language ? language.toUpperCase()
      : Object.hasOwn(languageNames, languageId) ? languageNames[languageId] : language;
    toolbar.append(element("span", languageLabel || "Код"));

    const pre = document.createElement("pre");
    pre.tabIndex = 0;
    const code = element("code");
    renderCode(code, token.text, language);
    pre.append(code);

    if (showCopy) {
      const copy = element("button", "Копировать", "code-copy");
      copy.type = "button";
      let timer;
      copy.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(token.text);
          copy.textContent = "Скопировано";
        } catch {
          window.getSelection()?.selectAllChildren(code);
          copy.textContent = "Код выделен";
        }
        clearTimeout(timer);
        timer = setTimeout(() => { copy.textContent = "Копировать"; }, 1800);
      });
      toolbar.append(copy);
    }

    block.append(toolbar, pre);
    return block;
  }

  function tableBlock(token, showCopy) {
    const scroll = element("div", "", "markdown-table-scroll");
    scroll.tabIndex = 0;
    scroll.setAttribute("role", "region");
    scroll.setAttribute("aria-label", "Таблица");
    const table = document.createElement("table");
    const addRow = (section, cells, header) => {
      const row = section.insertRow();
      for (const cell of cells) {
        const node = element(header ? "th" : "td");
        if (header)
          node.scope = "col";
        if (cell.align)
          node.style.textAlign = cell.align;
        appendTokens(node, cell.tokens, showCopy);
        row.append(node);
      }
    };
    addRow(table.createTHead(), token.header, true);
    const body = table.createTBody();
    token.rows.forEach(row => addRow(body, row, false));
    scroll.append(table);
    return scroll;
  }

  function appendTokens(parent, tokens, showCopy) {
    for (const token of tokens) {
      if (Object.hasOwn(inlineTags, token.type)) {
        parent.append(appendTokens(element(inlineTags[token.type]), token.tokens, showCopy));
        continue;
      }

      switch (token.type) {
        case "heading":
          parent.append(appendTokens(element("h" + Math.min(3 + token.depth, 6)), token.tokens, showCopy));
          break;
        case "list": {
          const list = element(token.ordered ? "ol" : "ul");
          if (token.ordered)
            list.start = token.start;
          for (const item of token.items) {
            const itemNode = element("li", "", item.task ? "markdown-task" : "");
            appendTokens(itemNode, item.tokens, showCopy);
            list.append(itemNode);
          }
          parent.append(list);
          break;
        }
        case "checkbox": {
          const checkbox = document.createElement("input");
          checkbox.type = "checkbox";
          checkbox.checked = token.checked;
          checkbox.disabled = true;
          checkbox.setAttribute("aria-label", token.checked ? "Выполнено" : "Ожидает выполнения");
          parent.append(checkbox, " ");
          break;
        }
        case "link":
          parent.append(link(token, appendTokens(document.createDocumentFragment(), token.tokens, showCopy)));
          break;
        case "image":
          parent.append(link(token, element("span", "Изображение · " + decodeEntities(token.text || token.href))));
          break;
        case "table":
          parent.append(tableBlock(token, showCopy));
          break;
        case "code":
          parent.append(codeBlock(token, showCopy));
          break;
        case "codespan":
          parent.append(element("code", token.text));
          break;
        case "hr":
        case "br":
          parent.append(element(token.type));
          break;
        case "html":
          parent.append(token.block ? element("div", token.text, "markdown-raw") : token.text);
          break;
        case "text":
          if (token.tokens)
            appendTokens(parent, token.tokens, showCopy);
          else
            parent.append(decodeEntities(token.text));
          break;
        case "escape":
          parent.append(token.text);
          break;
        case "space":
        case "def":
          break;
        default:
          parent.append(token.raw || "");
      }
    }
    return parent;
  }

  return {
    renderCode,
    render(target, source, showCopy) {
      try {
        const tokens = window.marked.lexer(source, { gfm: true });
        // Render lexer tokens as DOM nodes so model-generated HTML stays inert.
        target.replaceChildren(appendTokens(document.createDocumentFragment(), tokens, showCopy));
      } catch {
        target.replaceChildren(element("div", source, "markdown-raw"));
      }
    }
  };
})();
