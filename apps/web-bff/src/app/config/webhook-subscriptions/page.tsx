import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { ConfigAdmin } from "@/components/ConfigAdmin";
import { ApiError } from "@/lib/api";
import { listConfig } from "@/lib/config";
import type { ProductService, WebhookSubscription } from "@/lib/contracts";

export const dynamic = "force-dynamic";

const EVENTS = [
  { value: "document.completed", label: "document.completed" },
  { value: "document.failed", label: "document.failed" },
  { value: "document.purged", label: "document.purged" },
];

export default async function WebhookSubscriptionsPage() {
  let items: WebhookSubscription[] = [];
  let products: ProductService[] = [];
  let failure: string | null = null;

  try {
    [items, products] = await Promise.all([
      listConfig<WebhookSubscription>("webhook-subscriptions"),
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
      <h1>Webhooks</h1>
      <p className="subtitle">
        Avisos por POST quando um documento é concluído, falha ou é expurgado. O corpo é JSON e vem assinado no cabeçalho{" "}
        <span className="mono">X-Webhook-Signature</span> (<span className="mono">sha256=</span> + HMAC-SHA256 do corpo, com o
        segredo da assinatura). Falhou? Novas tentativas após 10 s, 30 s e 90 s; se a última falhar, o documento ganha o evento{" "}
        <span className="mono">WEBHOOK_DELIVERY_FAILED</span> na linha do tempo.
      </p>

      <AnonymousAccessBanner />

      <div className="alert alert--warning">
        Por segurança, endereços locais e de rede privada (localhost, 10.x, 192.168.x, a rede do Docker, o endereço de metadados
        da nuvem) são recusados, porque quem manda a requisição é o worker, de dentro da sua rede. Para testar contra um receptor na
        sua máquina, defina <span className="mono">WEBHOOK_ALLOW_PRIVATE_NETWORKS=true</span>. Para testar de graça e sem
        instalar nada, use uma URL do <span className="mono">webhook.site</span>.
      </div>

      {failure && <div className="alert alert--error">{failure}</div>}

      <ConfigAdmin
        resource="webhook-subscriptions"
        noun="webhook"
        emptyText="Nenhum webhook cadastrado."
        items={items}
        fields={[
          {
            name: "url",
            label: "URL",
            kind: "text",
            required: true,
            placeholder: "https://webhook.site/seu-identificador",
            help: "http ou https, sem usuário e senha na URL.",
          },
          {
            name: "secret",
            label: "Segredo de assinatura",
            kind: "password",
            createOnly: true,
            help: "Só na criação, de 16 a 256 caracteres; nunca é exibido depois. Deixe vazio para gerar um: ele aparece uma única vez, logo após criar.",
          },
          {
            name: "events",
            label: "Eventos",
            kind: "multiselect",
            required: true,
            defaultValue: "document.completed\ndocument.failed",
            options: EVENTS,
          },
          {
            name: "productServiceId",
            valuePath: "productService.id",
            label: "Só deste produto ou serviço",
            kind: "select",
            nullable: true,
            options: [
              { value: "", label: "Todos os documentos" },
              ...products.map((product) => ({ value: product.id, label: `${product.code} · ${product.name}` })),
            ],
          },
          { name: "active", label: "Ativo", kind: "boolean", defaultValue: true },
        ]}
        columns={[
          { header: "URL", key: "url" },
          { header: "Eventos", key: "events", kind: "list" },
          { header: "Produto", key: "productService.code", kind: "mono", empty: "todos" },
          { header: "Ativo", key: "active", kind: "boolean" },
          { header: "Criado em", key: "createdAt", kind: "instant" },
        ]}
      />
    </>
  );
}
