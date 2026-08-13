// RapidsolDestek shared front-end behaviors (grows per stage; S2 = language switch only).
"use strict";
document.addEventListener("change", (e) => {
  const sel = e.target.closest("select[data-lang-switch]");
  if (sel && sel.form) sel.form.submit();
});
