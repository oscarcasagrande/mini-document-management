"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

import type { ProblemDetails } from "@/lib/contracts";
import { bffRoutes } from "@/lib/routes";

interface DeleteDocumentButtonProps {
  documentId: string;
  protocol: string;
  /** Where to go after a successful deletion. Staying on a deleted document makes no sense. */
  redirectTo?: string;
}

/**
 * Deletion with the confirmation required by RF-014. The confirmation spells out that the file itself
 * is removed, because it is.
 */
export function DeleteDocumentButton({ documentId, protocol, redirectTo }: DeleteDocumentButtonProps) {
  const router = useRouter();
  const [isConfirming, setIsConfirming] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function remove() {
    setIsDeleting(true);
    setError(null);

    try {
      const response = await fetch(bffRoutes.detail(documentId), { method: "DELETE" });

      if (response.status === 204) {
        if (redirectTo) {
          router.push(redirectTo);
        }
        router.refresh();
        return;
      }

      const problem = (await response.json()) as ProblemDetails;
      setError(problem.detail ?? problem.title ?? `Falha com status ${response.status}.`);
    } catch {
      setError("Não foi possível falar com o serviço.");
    } finally {
      setIsDeleting(false);
      setIsConfirming(false);
    }
  }

  if (!isConfirming) {
    return (
      <>
        <button type="button" className="danger" onClick={() => setIsConfirming(true)}>
          Excluir
        </button>
        {error && <div className="alert alert--error">{error}</div>}
      </>
    );
  }

  return (
    <div className="alert alert--error">
      <p style={{ margin: "0 0 10px" }}>
        Excluir <span className="mono">{protocol}</span> em definitivo? Isso remove o registro, o
        histórico, os jobs e o arquivo original do volume. <strong>A ação é irreversível.</strong>
      </p>
      <div className="actions">
        <button type="button" className="danger" onClick={remove} disabled={isDeleting}>
          {isDeleting ? "Excluindo..." : "Confirmar exclusão"}
        </button>
        <button type="button" onClick={() => setIsConfirming(false)} disabled={isDeleting}>
          Cancelar
        </button>
      </div>
    </div>
  );
}
