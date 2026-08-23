/**
 * The whole screen when the account is signed in and this Kairos does not admit
 * it — the workbench replaced rather than decorated, as `Locked` replaces it.
 *
 * A separate screen rather than another wording of `Locked`: a session that ran
 * out is fixed by signing in again, and this is not. The identity provider was
 * happy to authenticate the account; Kairos does not recognise it as an
 * operator, so the fix is a group membership somebody with the provider has to
 * grant. A "Sign in" link here would send an operator round the whole browser
 * leg to land back on this same screen.
 *
 * No group name on screen: that is deployment configuration this bundle does not
 * know and should not guess — the server logs which group it expects at startup.
 *
 * No new CSS: `.page`, `.card`, `.card-narrow`, `.card-pad`, `.title-page` and
 * `.prose` are the same ones `Locked` draws.
 */
export default function NotOperator() {
  return (
    <div className="page">
      <main className="card card-narrow card-pad mt-7">
        <h1 className="title-page mb-3">This account is not an operator</h1>
        <p className="prose t-sm">
          You are signed in, and this Kairos does not recognise the account as one of its
          operators. Nothing here is broken and signing in again will not change it.
        </p>
        <p className="prose t-sm mt-5">
          Whoever runs this Kairos can fix it by adding the account to the group it admits. They
          will find which group that is in the server&rsquo;s startup log.
        </p>
      </main>
    </div>
  )
}
