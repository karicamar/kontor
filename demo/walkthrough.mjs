// Kontor walkthrough: drives one business day through the API and prints what happens.
// Start the API first (dotnet run --project src/Kontor.Api), then: node demo/walkthrough.mjs [http://localhost:5080]
// Needs Node 18+ (built-in fetch). Uses the development token endpoint, so it only works in Development.

import { mkdirSync, writeFileSync } from "node:fs";

const base = process.argv[2] ?? "http://localhost:5080";
const out = new URL("./out/", import.meta.url);
const eur = (n) => new Intl.NumberFormat("de-DE", { style: "currency", currency: "EUR" }).format(n);
const step = (title) => console.log(`\n\x1b[1;36m== ${title}\x1b[0m`);
const ok = (text) => console.log(`  \x1b[32m✔\x1b[0m ${text}`);
const no = (text) => console.log(`  \x1b[31m✘\x1b[0m ${text}`);

async function login(user, roles) {
  const r = await fetch(`${base}/dev/token`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ user, roles }) });
  if (!r.ok) throw new Error(`Cannot get a token (${r.status}). Is the API running in Development at ${base}?`);
  const { token } = await r.json();
  return async (method, path, body, headers = {}) => {
    const res = await fetch(base + path, {
      method,
      headers: { authorization: `Bearer ${token}`, ...(body !== undefined && typeof body !== "string" ? { "content-type": "application/json" } : {}), ...headers },
      body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
    });
    const text = await res.text();
    const data = (res.headers.get("content-type") ?? "").includes("json") ? JSON.parse(text) : text;
    return { status: res.status, data };
  };
}

const clara = await login("clara.clerk", ["clerk", "approver"]); // has both roles: still may not approve her own entries
const anton = await login("anton.approver", ["approver"]);
const cem = await login("cem.compliance", ["compliance"]);
const olga = await login("olga.ops", ["operations", "auditor"]);

const payments = (await olga("GET", "/api/payments")).data;
const accounts = (await olga("GET", "/api/accounts?kind=customer")).data;
const find = (name) => payments.find((p) => p.creditorName.startsWith(name));
if (!find("Muehlenwerk")) throw new Error("Expected the demo data (start with an empty database in Development).");

step("Four-eyes principle");
const mill = find("Muehlenwerk");
let r = await clara("POST", `/api/payments/${mill.id}/approve`);
no(`Clara approves her own ${eur(mill.amount)} transfer: ${r.status} ${r.data.title}`);
r = await anton("POST", `/api/payments/${mill.id}/approve`);
ok(`Anton approves it: ${r.data.status}`);
const deposit = payments.find((p) => p.creditorName === "Kunsthandel Weber" && p.amount === 6000);
r = await anton("POST", `/api/payments/${deposit.id}/approve`);
ok(`Anton approves Sofia's 6.000 € deposit for the painting: ${r.data.status}`);
const rest = payments.find((p) => p.creditorName === "Kunsthandel Weber" && p.amount === 5500);
r = await anton("POST", `/api/payments/${rest.id}/reject`, { reason: "Split payment: customer to confirm by phone first" });
no(`...but rejects the second part, flagged ${rest.riskFindings.map((f) => f.ruleId).join(", ")}: ${r.data.status}`);

step("AML: sanctions hit");
const lantern = find("Golden Lantern");
console.log(`  ${lantern.creditorName}, ${eur(lantern.amount)}: ${lantern.status} (${lantern.riskFindings[0].detail})`);
r = await anton("POST", `/api/payments/${lantern.id}/reject`, { reason: "not mine to decide" });
no(`An approver cannot decide on it: ${r.status} ${r.data.title}`);
r = await cem("POST", `/api/payments/${lantern.id}/reject`, { reason: "Confirmed match with the sanctions list. Reported to the FIU (goAML)." });
ok(`Compliance rejects it: ${r.data.status}, reserved money released`);

step("Verification of Payee");
const lukas = accounts.find((a) => a.holder === "Lukas Becker");
const anna = accounts.find((a) => a.holder === "Anna Schmidt");
const transfer = { debtorAccountId: lukas.id, creditorName: "Anna Schmitt", creditorIban: anna.iban, amount: 40, remittanceInformation: "Konzertkarte" };
r = await clara("POST", "/api/payments", transfer, { "Idempotency-Key": crypto.randomUUID() });
no(`Lukas pays "Anna Schmitt": ${r.data.verificationOfPayee}, did you mean "${r.data.suggestedName}"?`);
r = await clara("POST", "/api/payments", { ...transfer, creditorName: "Anna Schmidt" }, { "Idempotency-Key": "concert-ticket-1" });
ok(`Corrected: ${r.data.payeeVerification}, ${r.data.status} instantly (both accounts are in Kontor)`);
const firstId = r.data.id;
r = await clara("POST", "/api/payments", { ...transfer, creditorName: "Anna Schmidt" }, { "Idempotency-Key": "concert-ticket-1" });
(r.data.id === firstId ? ok : no)(`Network glitch, the app sends it again with the same Idempotency-Key: ${r.status}, ${r.data.id === firstId ? "same payment, booked once" : "DUPLICATE"}`);

step("SEPA file to the partner bank");
r = await olga("POST", "/api/sepa/batches");
ok(`${r.data.messageId}: ${r.data.transactions} transfers, ${eur(r.data.total)}, execution date ${r.data.executionDate}`);
const file = await olga("GET", `/api/sepa/batches/${r.data.id}/pain001`);
mkdirSync(out, { recursive: true });
writeFileSync(new URL(`${r.data.messageId}.pain.001.xml`, out), file.data);
ok(`pain.001.001.09 saved to demo/out/ (valid against the ISO 20022 schema, see the tests)`);

step("Bank statement (camt.053) comes back");
const submitted = (await olga("GET", "/api/payments?status=Submitted")).data;
const power = submitted.find((p) => p.creditorName.startsWith("Stadtwerke"));
const xml = (await olga("POST", "/dev/bank/statement", {
  returnEndToEndIds: [power.endToEndId], returnReason: "AC04",
  incoming: [
    { toIban: anna.iban.replaceAll(" ", ""), fromName: "Arbeitgeber Beispiel AG", fromIban: "DE89370400440532013000", amount: 2840, text: "Gehalt November 2026" },
    { toIban: "DE34099999009999999999", fromName: "Unbekannter Absender", fromIban: "DE89370400440532013000", amount: 75, text: "Rechnung 0815" },
  ],
})).data;
writeFileSync(new URL("statement.camt.053.xml", out), xml);
r = await olga("POST", "/api/sepa/statements", xml, { "content-type": "application/xml" });
for (const line of r.data.lines) console.log(`  ${line.direction.padEnd(6)} ${eur(line.amount).padStart(12)}  ${line.outcome}`);
(r.data.reconciled ? ok : no)(`Ledger nostro ${eur(r.data.nostroBalanceInLedger)} = bank closing balance ${eur(r.data.closingBalanceOnStatement)}`);
r = await olga("POST", "/api/sepa/statements", xml, { "content-type": "application/xml" });
no(`Importing the same statement again: ${r.status} ${r.data.title}`);

step("Controls");
const sheet = (await olga("GET", "/api/ledger/balance-sheet")).data;
(sheet.balanced ? ok : no)(`Balance sheet: assets ${eur(sheet.assets)} = liabilities ${eur(sheet.liabilitiesAndEquity)}, ${sheet.journalEntries} journal entries`);
const verify = (await olga("GET", "/api/audit/verify")).data;
(verify.intact ? ok : no)(`Audit trail: ${verify.recordsChecked} hash-chained records, ${verify.intact ? "intact" : "BROKEN at " + verify.firstBrokenSequence}`);
console.log("\nOpen the dashboard at " + base + " to see the same day in the browser.");
