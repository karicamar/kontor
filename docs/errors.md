# Error responses

Every error is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem response (`application/problem+json`) whose
`type` links to the matching section below. Unexpected errors return a generic 500 without details.

| Status | Type | When |
|---|---|---|
| 400 | <a id="bad-request"></a>`bad-request` | Malformed JSON or parameters |
| 403 | <a id="four-eyes"></a>`four-eyes` | You entered this payment (or released it as compliance officer) and try to approve it. The attempt is recorded in the audit trail. |
| 403 | <a id="compliance-only"></a>`compliance-only` | Only Compliance can decide on a payment blocked by an AML rule |
| 404 | <a id="not-found"></a>`not-found` | Account, payment or file does not exist |
| 409 | <a id="invalid-state"></a>`invalid-state` | The payment is not in a state that allows this action (e.g. approving it twice) |
| 409 | <a id="already-imported"></a>`already-imported` | A bank statement with this id was already booked |
| 413 | <a id="too-large"></a>`too-large` | Statement file above `Kontor:MaxStatementBytes` (5 MB) |
| 422 | <a id="validation"></a>`validation` | Invalid input; `detail` says what, `field` names the field when there is one |
| 422 | <a id="idempotency-key-reused"></a>`idempotency-key-reused` | The `Idempotency-Key` was already used by you for a *different* request |
| 422 | <a id="payee-name-mismatch"></a>`payee-name-mismatch` | Verification of Payee: the name does not match the account holder. `verificationOfPayee` is `CloseMatch` (with `suggestedName`) or `NoMatch`. Repeat with `confirmPayeeMismatch: true` to send anyway. |
| 422 | <a id="insufficient-funds"></a>`insufficient-funds` | Balance + overdraft limit − reserved amounts is less than the transfer |
| 422 | <a id="account-not-active"></a>`account-not-active` | The debtor account is frozen or closed |
| 422 | <a id="creditor-account-closed"></a>`creditor-account-closed` | The receiving Kontor account is closed |
| 422 | <a id="kyc-sanctions-hit"></a>`kyc-sanctions-hit` | Account opening refused: the holder is on the sanctions list |
| 422 | <a id="nothing-to-submit"></a>`nothing-to-submit` | No approved transfers are due for a SEPA file |
| 422 | <a id="wrong-account"></a>`wrong-account` | The statement is not for the platform's account at the partner bank |
| 422 | <a id="statement-inconsistent"></a>`statement-inconsistent` | Opening balance + entries ≠ closing balance: the file is incomplete or corrupt |
| 422 | <a id="one-statement"></a>`one-statement` | The file contains more or less than one statement |
| 422 | <a id="ledger"></a>`ledger` | A booking would break a ledger rule (unbalanced entry, overdraft) |
| 422 | <a id="sepa"></a>`sepa` | The SEPA file or statement breaks the SEPA rules (invalid XML, foreign currency, invalid ids) |
