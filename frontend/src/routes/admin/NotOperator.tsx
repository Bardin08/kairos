/**
 * The whole screen when the account is signed in and this Kairos does not admit
 * it, replacing the workbench the way `Locked` does.
 *
 * A separate screen rather than another wording of `Locked`: a session that ran
 * out is fixed by signing in again, and this is not. The fix is a group
 * membership somebody with the provider has to grant, so a "Sign in" link would
 * send an operator round the whole browser leg back to this screen. The group is
 * not named: it is deployment configuration this bundle should not guess, and
 * the server logs it at startup.
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
