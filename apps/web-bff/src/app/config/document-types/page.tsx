import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { ConfigAdmin } from "@/components/ConfigAdmin";
import { ApiError } from "@/lib/api";
import { listConfig } from "@/lib/config";
import type { DocumentType } from "@/lib/contracts";

export const dynamic = "force-dynamic";

export default async function DocumentTypesPage() {
  let items: DocumentType[] = [];
  let failure: string | null = null;

  try {
    items = await listConfig<DocumentType>("document-types");
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  return (
    <>
      <h1>Tipos documentais</h1>
      <p className="subtitle">
        O cadastro que a classificação e a extração usam para reconhecer cada tipo de documento. Alterar o schema, as
        regras de classificação ou as regras de extração aqui vale para o próximo documento classificado, sem exigir
        um novo deploy.
      </p>

      <AnonymousAccessBanner />

      <div className="alert alert--warning">
        Os sete tipos embutidos (<span className="mono">isBuiltIn</span>) podem ser desativados e editados, mas não
        excluídos.
      </div>

      {failure && <div className="alert alert--error">{failure}</div>}

      <ConfigAdmin
        resource="document-types"
        noun="tipo documental"
        emptyText="Nenhum tipo documental cadastrado."
        items={items}
        undeletable={{ key: "isBuiltIn", value: true, reason: "Tipo embutido: desative em vez de excluir." }}
        fields={[
          {
            name: "code",
            label: "Código",
            kind: "text",
            required: true,
            createOnly: true,
            placeholder: "BR_CPF_CARD",
            help: "Único, maiúsculo, até 64 caracteres (letras, dígitos, ponto, hífen ou sublinhado). Não muda depois de criado.",
          },
          { name: "name", label: "Nome", kind: "text", required: true, placeholder: "CPF" },
          {
            name: "schema",
            label: "Schema (JSON)",
            kind: "json",
            required: true,
            help: "JSON Schema completo dos campos do tipo documental.",
          },
          {
            name: "classificationRules",
            label: "Regras de classificação (JSON)",
            kind: "json",
            required: true,
            help: '{ evidence: [{ name, weight, patterns }], counterEvidence: [...], threshold }',
          },
          {
            name: "extractionRules",
            label: "Regras de extração (JSON)",
            kind: "json",
            required: true,
            help: "JSON livre e informativo, consumido pelo extrator do tipo.",
          },
          { name: "active", label: "Ativo", kind: "boolean", defaultValue: true },
        ]}
        columns={[
          { header: "Código", key: "code", kind: "mono" },
          { header: "Nome", key: "name" },
          { header: "Ativo", key: "active", kind: "boolean" },
          { header: "Embutido", key: "isBuiltIn", kind: "boolean" },
          { header: "Atualizado em", key: "updatedAt", kind: "instant" },
        ]}
      />
    </>
  );
}
