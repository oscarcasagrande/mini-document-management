"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState, useTransition } from "react";

import { DOCUMENT_STATUSES } from "@/lib/contracts";

const AUTO_REFRESH_MS = 5000;

/**
 * Filters of RF-005 plus the manual and automatic refresh. State lives in the query string, so a
 * filtered listing is a shareable URL and the server component does the querying.
 */
export function DocumentFilters({ hasInFlightDocuments }: { hasInFlightDocuments: boolean }) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [isPending, startTransition] = useTransition();
  const [autoRefresh, setAutoRefresh] = useState(true);

  const [form, setForm] = useState({
    protocol: searchParams.get("protocol") ?? "",
    fileName: searchParams.get("fileName") ?? "",
    documentType: searchParams.get("documentType") ?? "",
    productServiceCode: searchParams.get("productServiceCode") ?? "",
    externalReference: searchParams.get("externalReference") ?? "",
    channel: searchParams.get("channel") ?? "",
    status: searchParams.get("status") ?? "",
    uploadedFrom: searchParams.get("uploadedFrom") ?? "",
    uploadedTo: searchParams.get("uploadedTo") ?? "",
  });

  useEffect(() => {
    if (!autoRefresh || !hasInFlightDocuments) {
      return;
    }

    const timer = setInterval(() => router.refresh(), AUTO_REFRESH_MS);
    return () => clearInterval(timer);
  }, [autoRefresh, hasInFlightDocuments, router]);

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

    startTransition(() => router.push(`/documents?${next.toString()}`));
  }

  function clear() {
    setForm({
      protocol: "",
      fileName: "",
      documentType: "",
      productServiceCode: "",
      externalReference: "",
      channel: "",
      status: "",
      uploadedFrom: "",
      uploadedTo: "",
    });
    startTransition(() => router.push("/documents"));
  }

  return (
    <form className="card" onSubmit={apply}>
      <h2 className="card__title">Filtros</h2>
      <div className="field-grid">
        <div>
          <label htmlFor="filter-protocol">Protocolo</label>
          <input
            id="filter-protocol"
            type="text"
            value={form.protocol}
            placeholder="DOC-20260924-000001"
            onChange={(event) => setForm({ ...form, protocol: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-file-name">Nome do arquivo</label>
          <input
            id="filter-file-name"
            type="text"
            value={form.fileName}
            onChange={(event) => setForm({ ...form, fileName: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-document-type">Tipo documental</label>
          <input
            id="filter-document-type"
            type="text"
            value={form.documentType}
            placeholder="BR_CNH"
            onChange={(event) => setForm({ ...form, documentType: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-product">Produto ou serviço</label>
          <input
            id="filter-product"
            type="text"
            value={form.productServiceCode}
            placeholder="CONTA-PJ"
            onChange={(event) => setForm({ ...form, productServiceCode: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-external-reference">Referência externa</label>
          <input
            id="filter-external-reference"
            type="text"
            value={form.externalReference}
            maxLength={256}
            placeholder="PEDIDO-12345"
            onChange={(event) => setForm({ ...form, externalReference: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-channel">Canal</label>
          <select
            id="filter-channel"
            value={form.channel}
            onChange={(event) => setForm({ ...form, channel: event.target.value })}
          >
            <option value="">Todos</option>
            <option value="WEB">WEB</option>
            <option value="API">API</option>
          </select>
        </div>
        <div>
          <label htmlFor="filter-status">Status</label>
          <select
            id="filter-status"
            value={form.status}
            onChange={(event) => setForm({ ...form, status: event.target.value })}
          >
            <option value="">Todos</option>
            {DOCUMENT_STATUSES.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="filter-from">Enviado de</label>
          <input
            id="filter-from"
            type="date"
            value={form.uploadedFrom}
            onChange={(event) => setForm({ ...form, uploadedFrom: event.target.value })}
          />
        </div>
        <div>
          <label htmlFor="filter-to">Enviado até</label>
          <input
            id="filter-to"
            type="date"
            value={form.uploadedTo}
            onChange={(event) => setForm({ ...form, uploadedTo: event.target.value })}
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
        <button type="button" onClick={() => startTransition(() => router.refresh())} disabled={isPending}>
          Atualizar agora
        </button>
        <label style={{ display: "flex", gap: 6, alignItems: "center", fontWeight: 400, margin: 0 }}>
          <input
            type="checkbox"
            checked={autoRefresh}
            style={{ width: "auto" }}
            onChange={(event) => setAutoRefresh(event.target.checked)}
          />
          Atualizar automaticamente
        </label>
        {autoRefresh && hasInFlightDocuments && (
          <span style={{ fontSize: 13, color: "var(--text-muted)" }}>
            a cada {AUTO_REFRESH_MS / 1000}s enquanto houver documento em andamento
          </span>
        )}
      </div>
    </form>
  );
}
