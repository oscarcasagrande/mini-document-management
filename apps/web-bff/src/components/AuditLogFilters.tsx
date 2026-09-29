"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useState, useTransition } from "react";

/**
 * Filters of the read-only audit trail. State lives in the query string, so a filtered listing is a
 * shareable URL and the server component does the querying.
 */
export function AuditLogFilters() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [isPending, startTransition] = useTransition();

  const [form, setForm] = useState({
    userId: searchParams.get("userId") ?? "",
    action: searchParams.get("action") ?? "",
    from: searchParams.get("from") ?? "",
    to: searchParams.get("to") ?? "",
  });

  function apply(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const next = new URLSearchParams();
    for (const [key, value] of Object.entries(form)) {
      if (value.trim().length > 0) {
        next.set(key, value.trim());
      }
    }

    const pageSize = searchParams.get("pageSize");
    if (pageSize) {
      next.set("pageSize", pageSize);
    }

    startTransition(() => router.push(`/config/audit-logs?${next.toString()}`));
  }

  function clear() {
    setForm({ userId: "", action: "", from: "", to: "" });
    startTransition(() => router.push("/config/audit-logs"));
  }

  return (
    <form className="card" onSubmit={apply}>
      <h2 className="card__title">Filtros</h2>
      <div className="field-grid">
        <div>
          <label htmlFor="filter-user-id">Usuário</label>
          <input
            id="filter-user-id"
            type="text"
            value={form.userId}
            placeholder="Vazio até existir login"
            onChange={(event) => setForm({ ...form, userId: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-action">Ação</label>
          <input
            id="filter-action"
            type="text"
            value={form.action}
            placeholder="DOCUMENT_UPLOADED"
            onChange={(event) => setForm({ ...form, action: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-from">De</label>
          <input
            id="filter-from"
            type="date"
            value={form.from}
            onChange={(event) => setForm({ ...form, from: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-to">Até</label>
          <input
            id="filter-to"
            type="date"
            value={form.to}
            onChange={(event) => setForm({ ...form, to: event.target.value })}
          />
        </div>
      </div>

      <div className="actions">
        <button type="submit" className="primary" disabled={isPending}>
          Aplicar filtros
        </button>
        <button type="button" onClick={clear} disabled={isPending}>
          Limpar
        </button>
      </div>
    </form>
  );
}
