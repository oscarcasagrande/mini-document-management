import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { ConfigAdmin } from "@/components/ConfigAdmin";
import { ApiError } from "@/lib/api";
import { listConfig } from "@/lib/config";
import type { ProductService, RetentionPolicy } from "@/lib/contracts";

export const dynamic = "force-dynamic";

/** The types the classifier can produce, which are the ones a policy can name. */
const DOCUMENT_TYPES = [
  "BR_CPF_CARD",
  "BR_CIN",
  "BR_CNH",
  "BR_PROOF_OF_ADDRESS",
  "BR_CNPJ_CARD",
  "BR_CCMEI",
  "BR_SOCIAL_CONTRACT",
];

export default async function RetentionPoliciesPage() {
  let policies: RetentionPolicy[] = [];
  let products: ProductService[] = [];
  let failure: string | null = null;

  try {
    [policies, products] = await Promise.all([
      listConfig<RetentionPolicy>("retention-policies"),
      listConfig<ProductService>("product-services"),
    ]);
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  return (
    <>
      <h1>Políticas de retenção</h1>
      <p className="subtitle">
        Por quanto tempo os documentos são guardados antes de o arquivo original ser removido (expurgo). Vale a política
        mais específica: <strong>tipo + produto</strong>, depois <strong>produto</strong>, depois <strong>tipo</strong>,
        depois a <strong>global</strong>, que sempre existe.
      </p>

      <AnonymousAccessBanner />

      <div className="alert alert--warning">
        Criar ou alterar uma política <strong>não recalcula</strong> a data de expurgo dos documentos que já existem: eles a
        recebem ao serem reprocessados. Só o arquivo original é removido no expurgo; o histórico do documento fica.
      </div>

      {failure && <div className="alert alert--error">{failure}</div>}

      <ConfigAdmin
        resource="retention-policies"
        noun="política"
        emptyText="Nenhuma política cadastrada."
        items={policies}
        undeletable={{ key: "isGlobal", value: true, reason: "A política global não pode ser excluída, só alterada." }}
        fields={[
          {
            name: "documentType",
            label: "Tipo documental",
            kind: "select",
            createOnly: true,
            nullable: true,
            options: [
              { value: "", label: "Qualquer tipo" },
              ...DOCUMENT_TYPES.map((type) => ({ value: type, label: type })),
            ],
          },
          {
            name: "productServiceId",
            valuePath: "productService.id",
            label: "Produto ou serviço",
            kind: "select",
            createOnly: true,
            nullable: true,
            options: [
              { value: "", label: "Qualquer produto" },
              ...products.map((product) => ({ value: product.id, label: `${product.code} · ${product.name}` })),
            ],
            help: "Deixar os dois em \"qualquer\" é a política global, que já existe.",
          },
          {
            name: "retentionDays",
            label: "Dias de retenção",
            kind: "number",
            required: true,
            defaultValue: 365,
            help: "De 1 a 36500, contados do envio (ou do último reprocessamento).",
          },
        ]}
        columns={[
          { header: "Escopo", key: "scope" },
          { header: "Tipo", key: "documentType", empty: "qualquer" },
          { header: "Produto", key: "productService.code", kind: "mono", empty: "qualquer" },
          { header: "Dias", key: "retentionDays" },
          { header: "Atualizada em", key: "updatedAt", kind: "instant" },
        ]}
      />
    </>
  );
}
