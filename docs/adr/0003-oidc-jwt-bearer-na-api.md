# ADR 0003 — SSO da API por JWT bearer (`AddJwtBearer`), nunca `AddOpenIdConnect`

- **Status:** Aceita
- **Data:** 2026-09-29
- **Contexto do plano:** Pós-etapa 4 — hardening rumo à apresentação na SRM
- **Relacionada a:** PRD §21 (segurança mínima), `apps/api/Http/AnonymousAccessGateMiddleware.cs`,
  `docker-compose.yml`, `.env.example`

## Contexto

A PoC hoje não tem autenticação: `ALLOW_ANONYMOUS_ACCESS=false` apenas fecha `/api/v1` com `503`, porque
"não existe provedor de autenticação" (comentário original do middleware). Antes de apresentar a PoC,
ela precisa suportar SSO corporativo de verdade (Keycloak local para desenvolvimento, e depois um
Azure AD ou AD de produção), sem reescrever nada além de variáveis de ambiente quando o provedor trocar.

A arquitetura do sistema já responde por onde a autenticação entra: o navegador nunca fala com a API
`.NET` diretamente, só com o `web-bff` (Next.js), que chama a API do lado do servidor
(`src/lib/api.ts` é `server-only`). Um agente paralelo está implementando o fluxo de login do BFF
(NextAuth.js) e um contêiner Keycloak para desenvolvimento local; esta decisão cobre só o lado da API.

## Decisão

A API é um resource server puro: valida um token JWT bearer que alguém (o BFF, hoje; potencialmente um
outro cliente de máquina a máquina, amanhã) já obteve em algum fluxo de login. Ela nunca inicia login
interativo nem guarda sessão. Consequência direta: `Microsoft.AspNetCore.Authentication.JwtBearer`
(`AddJwtBearer`), nunca `Microsoft.AspNetCore.Authentication.OpenIdConnect` (`AddOpenIdConnect`) — esse
último é para uma aplicação dona de redirecionamento e cookie de sessão, que é o papel do BFF, não desta
API. `Authority` aponta para o emissor OIDC (ex.: uma realm do Keycloak) e a validação de emissor,
assinatura e expiração vem de graça, via `.well-known/openid-configuration` e JWKS.

### `DocReader:Security:Oidc` (`OIDC_*`)

Opção nova, no padrão de `apps/api/Options` (registro com `AddOptions<T>().Bind(...).ValidateOnStart()` e
um `IValidateOptions<T>` dedicado, como `SecretsOptionsValidator`):

| Campo | Variável | Padrão | Efeito |
|---|---|---|---|
| `Authority` | `OIDC_AUTHORITY` | vazio | Vazio = modo anônimo. Preenchido = registra `AddJwtBearer`. |
| `ClientId` | `OIDC_CLIENT_ID` | vazio | Client id desta API no provedor; também a audiência esperada do token. |
| `AdminRole` | `OIDC_ADMIN_ROLE` | `docreader-admin` | Papel exigido pelos três controllers de admin. |
| `UserRole` | `OIDC_USER_ROLE` | `docreader-user` | Papel de usuário comum. Reservado: nenhum endpoint o exige ainda. |

`ClientId` dobra como audiência (`ValidAudience`) em vez de uma quinta variável dedicada: é a leitura mais
simples de "para quem este token foi emitido", e evita configurar a mesma coisa duas vezes. Se um
provedor precisar de uma audiência diferente do client id da API (múltiplos clients atrás de uma
audiência de API comum), isso é uma extensão futura, não a forma comum. Vazio em `ClientId` só desliga a
validação de audiência; `Authority` sozinho já garante emissor e assinatura.

`OidcOptionsValidator` aceita `Authority` vazio (modo anônimo é uma configuração válida, não um erro) e
recusa uma `Authority` não vazia que não seja uma URL `http(s)` absoluta, ou um `AdminRole`/`UserRole`
vazio quando `Authority` está configurada — nenhum token satisfaria uma exigência de papel em branco.

### Modo anônimo é preservado exatamente, nunca combinado com o modo OIDC

`Authority` vazia: nenhum esquema JWT bearer é registrado, `AnonymousAccessGateMiddleware` continua
gatendo `/api` por `ALLOW_ANONYMOUS_ACCESS` exatamente como sempre fez, e a policy `AdminOnly` (ver
abaixo) é registrada como uma assertiva que sempre aceita — os três controllers de admin continuam
funcionando sem token algum.

`Authority` configurada: `AnonymousAccessGateMiddleware` sai da frente por completo (checa
`oidcOptions.Value.IsConfigured` logo no início e deixa passar) — o desafio 401 do JWT bearer é o portão
agora, e `ALLOW_ANONYMOUS_ACCESS` deixa de ter efeito. Os dois portões nunca se sobrepõem: para um dado
deploy, só um dos dois caminhos de código roda, o que era explicitamente pedido para manter os dois
casos simples de raciocinar.

Com `Authority` configurada, uma `FallbackPolicy` (`RequireAuthenticatedUser`) é registrada em
`AddAuthorization`, valendo para todo endpoint roteado que não carregue sua própria autorização — ou
seja, todo controller passa a exigir um token válido (qualquer papel), não só os três de admin. Isso é a
postura correta de "resource server ligado": uma vez que SSO está configurado, nenhuma chamada anônima é
esperada de lugar nenhum. `/health/live`, `/health/ready` e o redirecionamento `/` ganham `.AllowAnonymous()`
explícito, porque `FallbackPolicy` alcança qualquer endpoint mapeado, health checks inclusive, e o
Docker Compose precisa continuar enxergando o healthcheck sem token.

### RBAC nos três controllers de admin

`AdminBackupController`, `AdminRestoreController` e `AdminStorageMigrationController` ganharam
`[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`. A policy, não o atributo, decide o comportamento:
`AuthorizationPolicies.ConfigureAdminOnly` (chamada de `Program.cs`, testável isolada) registra ou uma
assertiva sempre-verdadeira (sem OIDC) ou `RequireAuthenticatedUser().RequireRole(oidcOptions.AdminRole)`
(com OIDC). O atributo nunca muda; só o que a policy exige muda, o que evita ramificar o código dos
controllers pelo modo. Nenhum outro controller (`storage-repositories`, `retention-policies`,
`document-types`, `webhook-subscriptions`, `product-services`, `documents`) ganhou `[Authorize(Roles=...)]`
— fora de escopo desta etapa (a exigência de **autenticação**, porém, já alcança esses controllers pela
`FallbackPolicy` quando OIDC está ligado; só a exigência de **papel de admin** ficou restrita aos três).
`StorageRepositoriesController.GetConnectionConfigAsync` já carregava um `TODO(RBAC)` prevendo exatamente
o papel `docreader-admin` — não foi tocado agora, mas é candidato natural do próximo pass de RBAC.

### 401 e 403 em `application/problem+json`, não o corpo padrão do framework

Por padrão, `AddJwtBearer` responde 401 sem token com um corpo texto puro (`WWW-Authenticate: Bearer
error="..."`) e delega 403 (papel insuficiente) ao mecanismo padrão de `Forbid`, nenhum dos dois no
formato RFC 9457 que o resto da API usa (`ApiExceptionHandler`). `JwtBearerEvents.OnChallenge` chama
`context.HandleResponse()` (suprime o corpo padrão) e escreve o problem+json via `AuthProblemWriter`
(`errorCode=UNAUTHENTICATED`); `OnForbidden` faz o mesmo para 403 (`errorCode=FORBIDDEN`). Ambos reusam
`ProblemDetailsFactory` e propagam `X-Correlation-Id`, igual a `ApiExceptionHandler` e
`ReadOnlyGateMiddleware`.

### Papéis do Keycloak: `realm_access.roles` precisa ser achatado

Keycloak (e a maioria dos IdPs baseados em realm) aninha os papéis do usuário numa claim `realm_access`
com a forma `{"roles": ["docreader-admin", ...]}`, não uma claim por papel. O tratamento padrão de JWT do
ASP.NET Core não sabe que essa claim carrega papéis: `ClaimsPrincipal.IsInRole` e
`[Authorize(Roles=...)]` nunca bateriam sem ajuda. `KeycloakRoleClaims.ExpandRealmRoles` lê essa claim e
gera uma claim `ClaimTypes.Role` por entrada do array; `KeycloakRealmRolesClaimsTransformation`
(`IClaimsTransformation`, registrada só quando OIDC está ligado) aplica isso ao principal autenticado
depois da validação do JWT. `MapInboundClaims = false` em `AddJwtBearer` mantém os nomes originais das
claims (`sub`, `email`, `realm_access`, ...) em vez do remapeamento legado do handler para URIs XML/WS-Fed.

### `GET /api/v1/me`

Devolve `{ userId, email, name, roles }` a partir do principal autenticado (`sub`, `email`,
`name`/`preferred_username`, e todo `ClaimTypes.Role`, incluindo os achatados do Keycloak). Em modo
anônimo não há token para ler: devolve `200` com um payload fixo (todo campo nulo, `roles` vazio) em vez
de `401`, coerente com o resto desta PoC anônima. Quando OIDC está ligado, a `FallbackPolicy` já garante
que nenhuma chamada sem token chegue à ação — o ramo anônimo do mapeador só é alcançado em modo anônimo.

### Swagger: botão "Authorize" só aparece com OIDC configurado

Com `Authority` vazia, a página de Swagger fica exatamente como sempre foi. Com `Authority` configurada,
`AddSecurityDefinition` registra um fluxo OAuth2 *authorization code + PKCE*, com as URLs de autorização e
token montadas pela convenção do Keycloak (`{authority}/protocol/openid-connect/{auth,token}`) — não há
chamada de descoberta (`.well-known/openid-configuration`) no start da API para isso, para não travar o
boot em uma dependência de rede; é só um atalho de conveniência para teste manual, não o caminho real de
validação de token (que é sempre via `Authority`/JWKS, dentro de `AddJwtBearer`). Um provedor que não siga
essa convenção de caminho (não-Keycloak) vai precisar desse trecho ajustado — ver "Gatilhos de revisão".

## Alternativas consideradas

| Alternativa | Por que não |
|---|---|
| `AddOpenIdConnect` na API | Middleware de app com login interativo e cookie de sessão. A API nunca inicia login: o navegador nem fala com ela. Usá-lo aqui duplicaria o papel do BFF e adicionaria estado de sessão a um resource server que deveria ser sem estado. |
| Fechar `/api` com 503 quando `ALLOW_ANONYMOUS_ACCESS=false`, sem JWT bearer nenhum | É o status quo; resolve "recusar tudo" mas não "aceitar quem tem token válido". Não é SSO, é só uma trava. |
| Uma quinta variável `OIDC_AUDIENCE` separada de `OIDC_CLIENT_ID` | Mais preciso para múltiplos clients atrás de uma audiência de API comum, mas essa PoC tem um client (o BFF) e uma API; a variável extra seria configuração sem uso real agora. Documentado acima como extensão futura. |
| Descoberta de `.well-known/openid-configuration` no start, para preencher as URLs do Swagger | Mais correto para IdPs não-Keycloak, mas adiciona uma chamada de rede síncrona ao boot da API só para um botão de conveniência de teste manual — desproporcional ao pedido ("keep this simple"). A convenção de caminho do Keycloak cobre o caso de uso real desta etapa (desenvolvimento local). |

## Consequências

**Positivas**

- Trocar de Keycloak local para Azure AD ou um AD de produção custa só variáveis de ambiente
  (`OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, papéis), zero mudança de código — o pedido original do dono do
  produto.
- Os dois modos (anônimo / OIDC) nunca coexistem no mesmo request: mais fácil de auditar e testar do que
  uma combinação de flags.
- RBAC nos três controllers de admin é o primeiro uso real do papel `docreader-admin` que já estava
  previsto num `TODO` do código de `StorageRepositoriesController`.

**Negativas e limites**

- `FallbackPolicy` exige autenticação (qualquer papel) em todo controller quando OIDC está ligado, não só
  nos três de admin. Isso é intencional (ver "Modo anônimo é preservado..."), mas é uma mudança de
  comportamento maior do que "só RBAC nos três admin": qualquer chamador do resto da API (documentos,
  tipos documentais, produtos, webhooks, repositórios de armazenamento, políticas de retenção) também
  passa a precisar de um token válido, só não de um papel específico. Nenhum desses seis controllers
  ganhou `[Authorize(Roles=...)]` — RBAC granular neles é trabalho futuro.
- As URLs do Swagger "Authorize" assumem a convenção de caminho do Keycloak. Um Azure AD real usa outra
  convenção (`/oauth2/v2.0/authorize`, `/oauth2/v2.0/token`); com Azure AD configurado, o botão não vai
  funcionar até esse trecho ganhar descoberta real ou variáveis de override explícitas
  (`OIDC_AUTHORIZATION_ENDPOINT`/`OIDC_TOKEN_ENDPOINT`, cogitadas e não implementadas nesta etapa).
- `RequireHttpsMetadata = !builder.Environment.IsDevelopment()` é explícito em `Program.cs`: o `JwtBearerOptions`
  do framework **não** infere isso do ambiente sozinho (o padrão é `true` incondicional) — a suposição original
  deste ADR de que ele o faria estava errada, e só apareceu ao subir o Keycloak de desenvolvimento de verdade
  (`InvalidOperationException: ... must use HTTPS unless disabled for development`). Um Keycloak servido em
  `http://` só funciona com `ASPNETCORE_ENVIRONMENT=Development` (como o `docker-compose.dev.yml` já define para
  `api`); o `docker-compose.yml` de aceite usa `Production` por padrão, então um Keycloak em HTTP atrás dele
  exigiria TLS ou `ASPNETCORE_ENVIRONMENT=Development` explícito.
- Este ADR não cobre o fluxo de login do BFF nem o contêiner Keycloak: são trabalho paralelo de outro
  agente, fora do escopo do lado `.NET`.

## Gatilhos de revisão

Reabrir esta decisão quando: um IdP que não seja Keycloak precisar do botão "Authorize" do Swagger
funcionando (adicionar descoberta real ou os overrides `OIDC_AUTHORIZATION_ENDPOINT`/`OIDC_TOKEN_ENDPOINT`);
RBAC granular for pedido nos outros seis controllers (`storage-repositories`, `retention-policies`,
`document-types`, `webhook-subscriptions`, `product-services`, `documents`); ou um cliente de máquina a
máquina (não o BFF) precisar chamar a API diretamente, o que pode justificar um segundo client id/audiência.
