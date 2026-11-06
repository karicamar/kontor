"use strict";
// Kontor demo back office. Plain JavaScript, no framework, no inline code (the CSP forbids it).
// Every value from the server is inserted with textContent, never innerHTML: payee names are user input.

const tokens = {};
let accounts = [];
let payKey = crypto.randomUUID();

const $ = (sel) => document.querySelector(sel);
const eur = (n) => new Intl.NumberFormat("de-DE", { style: "currency", currency: "EUR" }).format(n);
const time = (s) => new Date(s).toLocaleString("de-DE", { dateStyle: "short", timeStyle: "short" });

function el(tag, props = {}, ...children) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) e.append(c instanceof Node ? c : String(c));
  return e;
}
const pill = (text, cls = text) => el("span", { class: `pill s-${cls}` }, text.replace(/([a-z])([A-Z])/g, "$1 $2"));

function toast(message, error = false) {
  const t = $("#toast");
  t.textContent = message;
  t.className = error ? "toast error" : "toast";
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), 6000);
}

function currentUser() {
  const [user, roles] = $("#user").value.split("|");
  return { user, roles: roles.split(",") };
}

async function token() {
  const { user, roles } = currentUser();
  if (!tokens[user]) {
    const r = await fetch("/dev/token", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ user, roles }) });
    if (!r.ok) throw new Error("Dev tokens are disabled (run in Development).");
    tokens[user] = (await r.json()).token;
  }
  return tokens[user];
}

class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail ? `${problem.title}: ${problem.detail}` : problem?.title || `HTTP ${status}`);
    this.status = status;
    this.problem = problem || {};
  }
}

async function api(method, url, body, headers = {}) {
  const init = { method, headers: { authorization: `Bearer ${await token()}`, ...headers } };
  if (body !== undefined) {
    init.body = typeof body === "string" ? body : JSON.stringify(body);
    init.headers["content-type"] ??= "application/json";
  }
  const r = await fetch(url, init);
  const type = r.headers.get("content-type") || "";
  const data = type.includes("json") ? await r.json() : await r.text();
  if (r.status === 403) throw new ApiError(403, typeof data === "object" && data.title ? data : { title: "Your role is not allowed to do this" });
  if (!r.ok) throw new ApiError(r.status, typeof data === "object" ? data : { title: data });
  return data;
}

const holder = (id) => accounts.find((a) => a.id === id)?.holder ?? "?";

function riskBadge(score) {
  const cls = score >= 60 ? "bad" : score >= 25 ? "warn" : "ok";
  return el("span", { class: `risk ${cls}` }, score);
}

function findingsList(p) {
  if (!p.riskFindings.length) return el("div", { class: "sub" }, "No risk findings");
  return el("ul", {}, p.riskFindings.map((f) => el("li", {}, el("strong", {}, f.title), " ", el("span", { class: "rule" }, f.ruleId), el("div", { class: "sub" }, f.detail))));
}

// ---------- loaders ----------

async function loadAccounts() {
  accounts = await api("GET", "/api/accounts?kind=customer");
  const select = $("#pay-form [name=debtor]");
  const chosen = select.value;
  select.replaceChildren(...accounts.map((a) => el("option", { value: a.id }, `${a.holder} (${eur(a.available)} available)`)));
  if (chosen) select.value = chosen;
  $("#accounts tbody").replaceChildren(...accounts.map((a) => el("tr", {},
    el("td", {}, a.holder), el("td", { class: "mono" }, a.iban),
    el("td", { class: "num" }, eur(a.balance)), el("td", { class: "num" }, a.held ? eur(a.held) : "–"),
    el("td", { class: "num" }, eur(a.available)), el("td", {}, pill(a.status)))));
}

async function loadPayments() {
  const payments = await api("GET", "/api/payments?limit=200");
  $("#payment-list tbody").replaceChildren(...payments.map((p) => el("tr", {},
    el("td", {}, time(p.createdAt), el("div", { class: "sub mono" }, p.endToEndId)),
    el("td", {}, holder(p.debtorAccountId), " → ", el("strong", {}, p.creditorName), el("div", { class: "sub mono" }, p.creditorIban),
      p.remittanceInformation ? el("div", { class: "sub" }, p.remittanceInformation) : null),
    el("td", { class: "num" }, eur(p.amount)),
    el("td", {}, pill(p.status), p.returnReason ? el("div", { class: "sub" }, p.returnReason) : null, p.rejectReason ? el("div", { class: "sub" }, p.rejectReason) : null),
    el("td", {}, riskBadge(p.riskScore), " ", el("span", { class: "rule" }, p.riskFindings.map((f) => f.ruleId).join(" "))),
    el("td", {}, pill(p.payeeVerification)),
    el("td", { class: "sub" }, p.createdBy, p.approvedBy ? ` / ${p.approvedBy}` : ""))));

  const pending = payments.filter((p) => p.status === "PendingApproval");
  const blocked = payments.filter((p) => p.status === "Blocked");
  $("#queue-count").textContent = pending.length || "";
  $("#blocked-count").textContent = blocked.length || "";
  $("#queue-list").replaceChildren(...(pending.length ? pending.map((p) => decisionCard(p, false)) : [el("div", { class: "empty" }, "Nothing waiting.")]));
  $("#blocked-list").replaceChildren(...(blocked.length ? blocked.map((p) => decisionCard(p, true)) : [el("div", { class: "empty" }, "No blocked transfers.")]));
}

function decisionCard(p, blocked) {
  const actions = blocked
    ? [el("button", { onclick: () => decide(p, "release", "Why can the block be released? (documented in the audit trail)") }, "Release to approval"),
       el("button", { class: "danger", onclick: () => decide(p, "reject", "Reason for rejecting") }, "Reject")]
    : [el("button", { onclick: () => decide(p, "approve") }, "Approve"),
       el("button", { class: "secondary", onclick: () => decide(p, "reject", "Reason for rejecting") }, "Reject")];
  return el("div", { class: blocked ? "item blocked" : "item" },
    el("div", {},
      el("div", { class: "amount" }, eur(p.amount)),
      el("div", {}, holder(p.debtorAccountId), " → ", el("strong", {}, p.creditorName), " ", el("span", { class: "mono sub" }, p.creditorIban)),
      el("div", { class: "sub" }, `Entered by ${p.createdBy} · ${time(p.createdAt)}`, p.complianceNote ? ` · Compliance (${p.complianceDecisionBy}): ${p.complianceNote}` : ""),
      p.amount >= 10000 ? el("div", { class: "sub" }, "Above the 10.000 € four-eyes limit") : null,
      findingsList(p)),
    el("div", { class: "actions" }, actions));
}

async function decide(p, action, question) {
  let body;
  if (question) {
    const reason = prompt(question);
    if (!reason) return;
    body = { reason };
  }
  try {
    const result = await api("POST", `/api/payments/${p.id}/${action}`, body ?? {});
    toast(`${p.creditorName}: ${result.status.replace(/([a-z])([A-Z])/g, "$1 $2")}`);
    await refresh();
  } catch (e) {
    toast(e.message, true);
    await refresh();
  }
}

async function loadSepa() {
  try {
    const batches = await api("GET", "/api/sepa/batches");
    $("#batches tbody").replaceChildren(...batches.map((b) => el("tr", {},
      el("td", { class: "mono" }, b.messageId), el("td", {}, b.executionDate),
      el("td", { class: "num" }, b.transactions), el("td", { class: "num" }, eur(b.total)),
      el("td", {}, el("button", { class: "small secondary", onclick: () => download(b) }, "pain.001")))));
    const items = await api("GET", "/api/reconciliation/open-items");
    $("#open-items tbody").replaceChildren(...(items.length ? items.map((o) => el("tr", {},
      el("td", { class: "mono" }, o.reference), el("td", {}, o.direction), el("td", { class: "num" }, eur(o.amount)),
      el("td", {}, o.counterpartyName ?? "–"), el("td", {}, o.reason))) : [el("tr", {}, el("td", { colspan: 5, class: "empty" }, "No open items."))]));
  } catch (e) {
    if (e.status !== 403) throw e;
    $("#batches tbody").replaceChildren(el("tr", {}, el("td", { colspan: 5, class: "empty" }, "Sign in as Operations to see SEPA files.")));
  }
}

async function download(batch) {
  const r = await fetch(`/api/sepa/batches/${batch.id}/pain001`, { headers: { authorization: `Bearer ${await token()}` } });
  const url = URL.createObjectURL(await r.blob());
  el("a", { href: url, download: `${batch.messageId}.pain.001.xml` }).click();
  URL.revokeObjectURL(url);
}

async function loadLedger() {
  try {
    const s = await api("GET", "/api/ledger/balance-sheet");
    $("#sheet-status").replaceChildren(s.balanced ? pill("Books balance", "Approved") : pill(`${s.problems.length} problems`, "Blocked"));
    const side = (name) => s.sides.find((x) => x.side === name) ?? { accounts: [], total: 0 };
    const table = (title, sides) => el("div", {},
      el("h3", {}, title),
      el("table", {}, el("tbody", {}, sides.flatMap((x) => side(x).accounts).map((a) => el("tr", {},
        el("td", {}, a.holder, a.code ? el("div", { class: "sub mono" }, a.code) : null), el("td", { class: "num" }, eur(a.balance)))))));
    $("#sheet").replaceChildren(
      el("div", { class: "sheet" }, table("Assets", ["Asset"]), table("Liabilities and equity", ["Liability", "Equity"])),
      el("div", { class: "totals" }, el("span", {}, `Assets ${eur(s.assets)}`), el("span", {}, `Liabilities + equity ${eur(s.liabilitiesAndEquity)}`), el("span", { class: "sub" }, `${s.journalEntries} journal entries`)),
      s.problems.length ? el("ul", {}, s.problems.map((p) => el("li", { class: "bad" }, p))) : el("p", { class: "hint" }, "Every journal entry balances, and every account balance equals the sum of its postings."));
  } catch (e) {
    if (e.status !== 403) throw e;
    $("#sheet").replaceChildren(el("div", { class: "empty" }, "Sign in as Operations or Audit to see the books."));
  }
}

async function loadAudit() {
  try {
    const records = await api("GET", "/api/audit?limit=1000");
    $("#audit-list tbody").replaceChildren(...records.reverse().map((r) => el("tr", {},
      el("td", { class: "num" }, r.sequence), el("td", { class: "sub" }, new Date(r.at).toISOString().replace("T", " ").slice(0, 19)),
      el("td", {}, r.actor), el("td", {}, el("strong", { class: r.action.includes("denied") || r.action.startsWith("aml") ? "bad" : "" }, r.action)),
      el("td", { class: "mono sub" }, r.details.length > 140 ? r.details.slice(0, 140) + "…" : r.details),
      el("td", { class: "mono sub" }, r.hash.slice(0, 12)))));
    await verifyChain();
  } catch (e) {
    if (e.status !== 403) throw e;
    $("#audit-list tbody").replaceChildren(el("tr", {}, el("td", { colspan: 6, class: "empty" }, "Sign in as Operations + Audit to read the audit trail.")));
  }
}

async function refresh() {
  try {
    await loadAccounts();
    await Promise.all([loadPayments(), loadSepa(), loadLedger(), loadAudit()]);
  } catch (e) {
    toast(e.message, true);
  }
}

// ---------- actions ----------

$("#pay-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const f = new FormData(ev.target);
  const request = {
    debtorAccountId: f.get("debtor"), creditorName: f.get("name"), creditorIban: f.get("iban"),
    amount: Number(f.get("amount")), remittanceInformation: f.get("text") || null,
    requestedExecutionDate: f.get("date") || null, confirmPayeeMismatch: f.get("confirm") === "on",
  };
  try {
    const p = await api("POST", "/api/payments", request, { "Idempotency-Key": payKey });
    payKey = crypto.randomUUID();
    $("#confirm-row").hidden = true;
    ev.target.reset();
    toast(`Transfer to ${p.creditorName}: ${p.status.replace(/([a-z])([A-Z])/g, "$1 $2")}${p.riskFindings.length ? ` (${p.riskFindings.map((x) => x.ruleId).join(", ")})` : ""}`);
    await refresh();
  } catch (e) {
    if (e.problem.verificationOfPayee) {
      $("#confirm-row").hidden = false;
      payKey = crypto.randomUUID(); // a confirmed retry is a different request
      toast(e.problem.suggestedName ? `Verification of Payee: the account belongs to "${e.problem.suggestedName}".` : "Verification of Payee: the name does not match this IBAN.", true);
    } else toast(e.message, true);
  }
});

$("#make-batch").addEventListener("click", async () => {
  try {
    const b = await api("POST", "/api/sepa/batches");
    toast(`SEPA file ${b.messageId}: ${b.transactions} transfers, ${eur(b.total)}`);
  } catch (e) { toast(e.message, true); }
  await refresh();
});

$("#import-statement").addEventListener("click", async () => {
  try {
    const submitted = await api("GET", "/api/payments?status=Submitted");
    const first = accounts[0];
    const xml = await api("POST", "/dev/bank/statement", {
      returnEndToEndIds: submitted.length ? [submitted[submitted.length - 1].endToEndId] : [],
      returnReason: "AC04",
      incoming: [
        { toIban: first.iban.replaceAll(" ", ""), fromName: "Arbeitgeber Beispiel AG", fromIban: "DE89370400440532013000", amount: 2840, text: "Gehalt November 2026" },
        { toIban: "DE34099999009999999999", fromName: "Unbekannter Absender", fromIban: "DE89370400440532013000", amount: 75, text: "Rechnung 0815" },
      ],
    });
    const result = await api("POST", "/api/sepa/statements", xml, { "content-type": "application/xml" });
    $("#import-result").replaceChildren(
      el("p", {}, `Statement ${result.statementId}: ${result.entries} lines, ${result.matched} matched, ${result.openItems} open. `,
        result.reconciled ? pill("Ledger matches the bank", "Approved") : pill("Difference to the bank", "Blocked")),
      el("table", { class: "result-lines" }, el("tbody", {}, result.lines.map((l) => el("tr", {},
        el("td", {}, l.direction), el("td", { class: "num" }, eur(l.amount)), el("td", {}, l.outcome))))));
  } catch (e) { toast(e.message, true); }
  await refresh();
});

async function verifyChain() {
  try {
    const v = await api("GET", "/api/audit/verify");
    $("#verify-result").replaceChildren(v.intact
      ? pill(`Intact: ${v.recordsChecked} records`, "Approved")
      : pill(`Broken at #${v.firstBrokenSequence}: ${v.problem}`, "Blocked"));
  } catch (e) { if (e.status !== 403) toast(e.message, true); }
}
$("#verify").addEventListener("click", verifyChain);

$("#tabs").addEventListener("click", (ev) => {
  const button = ev.target.closest("button");
  if (!button) return;
  document.querySelectorAll("#tabs button").forEach((b) => b.classList.toggle("active", b === button));
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.id === button.dataset.tab));
});

$("#user").addEventListener("change", refresh);

// Open a tab and user from the address, e.g. /#queue@anton.approver (handy for screenshots)
const [hashTab, hashUser] = location.hash.slice(1).split("@");
if (hashUser) for (const o of $("#user").options) if (o.value.startsWith(hashUser + "|")) $("#user").value = o.value;
if (hashTab) document.querySelector(`#tabs button[data-tab="${hashTab}"]`)?.click();
refresh();
