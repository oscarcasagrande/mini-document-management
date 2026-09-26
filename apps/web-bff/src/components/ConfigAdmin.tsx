"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

import type { ConfigResource } from "@/lib/config-resources";
import type { ProblemDetails } from "@/lib/contracts";
import { formatInstant } from "@/lib/format";

export type AdminFieldKind = "text" | "number" | "boolean" | "select" | "multiselect" | "password" | "secretJson" | "tags";

export interface AdminField {
  /** Key of the request body. */
  name: string;
  /** Where the current value is in the API resource, when it is not a property with this name (for example "productService.id"). */
  valuePath?: string;
  label: string;
  kind: AdminFieldKind;
  required?: boolean;
  /** Only sent when creating; shown read-only when editing (for example a unique code). */
  createOnly?: boolean;
  /** Empty means null instead of an empty string or a missing value. */
  nullable?: boolean;
  options?: { value: string; label: string }[];
  help?: string;
  placeholder?: string;
  defaultValue?: string | number | boolean;
}

export type AdminColumnKind = "text" | "mono" | "boolean" | "instant" | "list";

export interface AdminColumn {
  header: string;
  /** Property of the item, or a dotted path such as productService.code. */
  key: string;
  kind?: AdminColumnKind;
  empty?: string;
}

/** Any resource returned by the API: only the id is assumed, the rest is read by property name. */
type Item = { id: string };

interface ConfigAdminProps {
  resource: ConfigResource;
  /** Singular, lower case, for the messages: "produto". */
  noun: string;
  fields: AdminField[];
  columns: AdminColumn[];
  items: Item[];
  /** An item whose property has this value cannot be deleted, and the button says why. */
  undeletable?: { key: string; value: unknown; reason: string };
  emptyText: string;
}

type Values = Record<string, string | boolean>;

function pathValue(item: object, key: string): unknown {
  return key.split(".").reduce<unknown>((current, part) => {
    return current !== null && typeof current === "object" ? (current as Record<string, unknown>)[part] : undefined;
  }, item);
}

function initialValues(fields: AdminField[], item: Item | null): Values {
  const values: Values = {};

  for (const field of fields) {
    const current = item ? pathValue(item, field.valuePath ?? field.name) : undefined;

    if (field.kind === "boolean") {
      values[field.name] = typeof current === "boolean" ? current : Boolean(field.defaultValue ?? false);
    } else if (field.kind === "tags" || field.kind === "multiselect") {
      values[field.name] = Array.isArray(current) ? current.join("\n") : String(field.defaultValue ?? "");
    } else if (field.kind === "password" || field.kind === "secretJson") {
      // Secrets never come back from the API, so there is nothing to show: the field only overwrites.
      values[field.name] = "";
    } else {
      values[field.name] = current === null || current === undefined ? String(field.defaultValue ?? "") : String(current);
    }
  }

  return values;
}

function buildBody(fields: AdminField[], values: Values, isEditing: boolean): Record<string, unknown> {
  const body: Record<string, unknown> = {};

  for (const field of fields) {
    if (isEditing && field.createOnly) {
      continue;
    }

    const raw = values[field.name];

    switch (field.kind) {
      case "boolean":
        body[field.name] = raw === true;
        break;
      case "number": {
        const text = String(raw).trim();
        body[field.name] = text === "" ? null : Number(text);
        break;
      }
      case "tags":
      case "multiselect":
        body[field.name] = String(raw)
          .split(/[\n,]/)
          .map((entry) => entry.trim())
          .filter((entry) => entry.length > 0);
        break;
      case "password": {
        const text = String(raw);
        if (text.length > 0) {
          body[field.name] = text;
        }
        break;
      }
      case "secretJson": {
        const text = String(raw).trim();
        if (text.length > 0) {
          body[field.name] = JSON.parse(text) as unknown;
        }
        break;
      }
      default: {
        const text = String(raw).trim();
        body[field.name] = text === "" && field.nullable ? null : text;
      }
    }
  }

  return body;
}

function renderCell(item: Item, column: AdminColumn) {
  const value = pathValue(item, column.key);

  if (value === null || value === undefined || value === "") {
    return <span className="muted">{column.empty ?? "—"}</span>;
  }

  switch (column.kind) {
    case "boolean":
      return <span className={`chip chip--${value === true ? "done" : "pending"}`}>{value === true ? "Sim" : "Não"}</span>;
    case "instant":
      return formatInstant(String(value));
    case "list":
      return Array.isArray(value) ? value.join(", ") : String(value);
    case "mono":
      return <span className="mono">{String(value)}</span>;
    default:
      return String(value);
  }
}

/**
 * Administration screen of one configuration resource: the list, a form to create or edit, and a delete with
 * confirmation. It is driven by data (fields and columns) so the four cadastros look and behave the same.
 * Secret fields are write-only: they start empty, and leaving one empty when editing keeps the stored value.
 */
export function ConfigAdmin({ resource, noun, fields, columns, items, undeletable, emptyText }: ConfigAdminProps) {
  const router = useRouter();
  const [editing, setEditing] = useState<Item | "new" | null>(null);
  const [values, setValues] = useState<Values>({});
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  /** A secret the API generated and will never show again: shown once, until the next action. */
  const [revealedSecret, setRevealedSecret] = useState<string | null>(null);

  const isEditing = editing !== null && editing !== "new";

  function open(target: Item | "new") {
    setEditing(target);
    setValues(initialValues(fields, target === "new" ? null : target));
    setError(null);
    setNotice(null);
    setRevealedSecret(null);
  }

  function close() {
    setEditing(null);
    setError(null);
  }

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsSaving(true);
    setError(null);

    try {
      const body = buildBody(fields, values, isEditing);
      const response = await fetch(
        isEditing ? `/api/bff/config/${resource}/${encodeURIComponent((editing as Item).id)}` : `/api/bff/config/${resource}`,
        {
          method: isEditing ? "PUT" : "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
        },
      );

      if (response.ok) {
        const answer = (await response.json()) as { secret?: unknown };
        setRevealedSecret(typeof answer.secret === "string" ? answer.secret : null);
        setNotice(isEditing ? `Alterações salvas.` : `Cadastro criado.`);
        setEditing(null);
        router.refresh();
        return;
      }

      const problem = (await response.json()) as ProblemDetails;
      setError(problem.detail ?? problem.title ?? `Falha com status ${response.status}.`);
    } catch (failure) {
      setError(failure instanceof SyntaxError ? "O JSON informado é inválido." : "Não foi possível falar com o serviço.");
    } finally {
      setIsSaving(false);
    }
  }

  async function remove(id: string) {
    setError(null);
    setNotice(null);

    try {
      const response = await fetch(`/api/bff/config/${resource}/${encodeURIComponent(id)}`, { method: "DELETE" });

      if (response.status === 204) {
        setNotice(`Cadastro excluído.`);
        router.refresh();
      } else {
        const problem = (await response.json()) as ProblemDetails;
        setError(problem.detail ?? problem.title ?? `Falha com status ${response.status}.`);
      }
    } catch {
      setError("Não foi possível falar com o serviço.");
    } finally {
      setConfirmingId(null);
    }
  }

  return (
    <>
      <div className="actions" style={{ marginBottom: 14 }}>
        <button type="button" className="primary" onClick={() => open("new")} disabled={editing !== null}>
          Novo {noun}
        </button>
      </div>

      {notice && <div className="alert alert--success">{notice}</div>}
      {revealedSecret && (
        <div className="alert alert--warning">
          <strong>Guarde este segredo agora.</strong> Ele foi gerado porque nenhum foi informado, é guardado cifrado e não aparece
          de novo em lugar nenhum:
          <div className="mono" style={{ marginTop: 8, wordBreak: "break-all", userSelect: "all" }}>
            {revealedSecret}
          </div>
        </div>
      )}
      {error && editing === null && <div className="alert alert--error">{error}</div>}

      {editing !== null && (
        <form className="card" onSubmit={submit}>
          <h2 className="card__title">{isEditing ? `Editar ${noun}` : `Novo ${noun}`}</h2>
          <div className="field-grid">
            {fields.map((field) => {
              const id = `${resource}-${field.name}`;
              const readOnly = isEditing && field.createOnly === true;
              const value = values[field.name];

              return (
                <div key={field.name}>
                  {field.kind === "boolean" ? (
                    <label htmlFor={id} style={{ display: "flex", gap: 8, alignItems: "center", fontWeight: 400 }}>
                      <input
                        id={id}
                        type="checkbox"
                        style={{ width: "auto" }}
                        checked={value === true}
                        onChange={(event) => setValues({ ...values, [field.name]: event.target.checked })}
                      />
                      {field.label}
                    </label>
                  ) : (
                    <>
                      <label htmlFor={id}>
                        {field.label}
                        {field.required && !readOnly ? " *" : ""}
                      </label>
                      {field.kind === "select" ? (
                        <select
                          id={id}
                          value={String(value ?? "")}
                          disabled={readOnly}
                          onChange={(event) => setValues({ ...values, [field.name]: event.target.value })}
                        >
                          {(field.options ?? []).map((option) => (
                            <option key={option.value} value={option.value}>
                              {option.label}
                            </option>
                          ))}
                        </select>
                      ) : field.kind === "multiselect" ? (
                        <div style={{ display: "grid", gap: 6 }}>
                          {(field.options ?? []).map((option) => {
                            const chosen = String(value ?? "").split("\n").filter((entry) => entry.length > 0);
                            const checked = chosen.includes(option.value);

                            return (
                              <label key={option.value} style={{ display: "flex", gap: 8, alignItems: "center", fontWeight: 400 }}>
                                <input
                                  type="checkbox"
                                  style={{ width: "auto" }}
                                  checked={checked}
                                  onChange={(event) => {
                                    const next = event.target.checked
                                      ? [...chosen, option.value]
                                      : chosen.filter((entry) => entry !== option.value);
                                    setValues({ ...values, [field.name]: next.join("\n") });
                                  }}
                                />
                                <span className="mono">{option.label}</span>
                              </label>
                            );
                          })}
                        </div>
                      ) : field.kind === "tags" || field.kind === "secretJson" ? (
                        <textarea
                          id={id}
                          rows={field.kind === "tags" ? 3 : 4}
                          value={String(value ?? "")}
                          placeholder={field.kind === "secretJson" && isEditing ? "•••••• (deixe vazio para manter)" : field.placeholder}
                          autoComplete="off"
                          spellCheck={false}
                          style={field.kind === "secretJson" ? ({ WebkitTextSecurity: "disc" } as React.CSSProperties) : undefined}
                          onChange={(event) => setValues({ ...values, [field.name]: event.target.value })}
                        />
                      ) : (
                        <input
                          id={id}
                          type={field.kind === "password" ? "password" : field.kind === "number" ? "number" : "text"}
                          value={String(value ?? "")}
                          readOnly={readOnly}
                          required={field.required && !readOnly && !(field.kind === "password" && isEditing)}
                          placeholder={field.kind === "password" && isEditing ? "•••••• (deixe vazio para manter)" : field.placeholder}
                          autoComplete={field.kind === "password" ? "new-password" : "off"}
                          onChange={(event) => setValues({ ...values, [field.name]: event.target.value })}
                        />
                      )}
                    </>
                  )}
                  {field.help && <div className="small muted">{field.help}</div>}
                </div>
              );
            })}
          </div>

          {error && <div className="alert alert--error">{error}</div>}

          <div className="actions">
            <button type="submit" className="primary" disabled={isSaving}>
              {isSaving ? "Salvando..." : "Salvar"}
            </button>
            <button type="button" onClick={close} disabled={isSaving}>
              Cancelar
            </button>
          </div>
        </form>
      )}

      {items.length === 0 ? (
        <div className="card">
          <div className="empty">{emptyText}</div>
        </div>
      ) : (
        <div className="card table-scroll">
          <table>
            <thead>
              <tr>
                {columns.map((column) => (
                  <th key={column.key}>{column.header}</th>
                ))}
                <th>Ações</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => {
                const blocked = undeletable !== undefined && pathValue(item, undeletable.key) === undeletable.value;

                return (
                  <tr key={item.id}>
                    {columns.map((column) => (
                      <td key={column.key}>{renderCell(item, column)}</td>
                    ))}
                    <td>
                      {confirmingId === item.id ? (
                        <div className="actions">
                          <button type="button" className="danger" onClick={() => remove(item.id)}>
                            Confirmar exclusão
                          </button>
                          <button type="button" onClick={() => setConfirmingId(null)}>
                            Cancelar
                          </button>
                        </div>
                      ) : (
                        <div className="actions">
                          <button type="button" onClick={() => open(item)} disabled={editing !== null}>
                            Editar
                          </button>
                          <button
                            type="button"
                            className="danger"
                            disabled={blocked || editing !== null}
                            title={blocked ? undeletable?.reason : undefined}
                            onClick={() => setConfirmingId(item.id)}
                          >
                            Excluir
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
