import { isOidcConfigured } from "@/lib/auth-config";

/**
 * The warning required by RF-001 and by section 9 of the PRD. It is deliberately impossible to miss:
 * anyone who reaches this page can read every document that was ever uploaded.
 *
 * Skipped once OIDC login is configured: at that point the claim it makes ("não há login") is simply
 * false, and every page it appears on already sits behind proxy.ts's session gate.
 */
export function AnonymousAccessBanner() {
  if (isOidcConfigured) {
    return null;
  }

  return (
    <aside className="banner" role="note">
      <strong>Ambiente sem controle de acesso</strong>
      Esta é uma PoC local. Não há login, cadastro nem autorização: qualquer pessoa com acesso a esta
      URL pode enviar, listar, visualizar, baixar e excluir qualquer documento.
      <ul>
        <li>Não publique esta aplicação na internet.</li>
        <li>Use apenas documentos sintéticos, mascarados ou autorizados.</li>
        <li>Em uma versão produtiva, isto deve ser substituído por autenticação e autorização.</li>
      </ul>
    </aside>
  );
}
