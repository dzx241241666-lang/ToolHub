package dev.switchboard.send;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.math.BigInteger;
import java.net.Socket;
import java.security.*;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.util.Date;
import javax.net.ssl.*;
import javax.security.auth.x500.X500Principal;

/** A single, app-owned ECDSA identity usable by Android's TLS provider. */
final class AndroidIdentity {
    private static final String ALIAS = "switchboard-send-ec-v2";
    final SSLContext tls;
    final String fingerprint;

    AndroidIdentity() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (!store.containsAlias(ALIAS)) {
            KeyPairGenerator generator = KeyPairGenerator.getInstance("EC", "AndroidKeyStore");
            generator.initialize(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN | KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                // Conscrypt supplies the already hashed digest to NONEwithECDSA.
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                .setCertificateSubject(new X500Principal("CN=Switchboard Send"))
                .setCertificateSerialNumber(BigInteger.valueOf(System.currentTimeMillis()))
                .setCertificateNotBefore(new Date(System.currentTimeMillis() - 86400000L))
                .setCertificateNotAfter(new Date(System.currentTimeMillis() + 10L * 365 * 86400000L))
                .build());
            generator.generateKeyPair();
        }
        PrivateKey privateKey = (PrivateKey) store.getKey(ALIAS, null);
        java.security.cert.Certificate[] certificates = store.getCertificateChain(ALIAS);
        X509Certificate[] chain = new X509Certificate[certificates.length];
        for (int i = 0; i < chain.length; i++) chain[i] = (X509Certificate) certificates[i];
        chain[0].checkValidity();
        fingerprint = PeerEngine.hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()));
        X509ExtendedKeyManager manager = new X509ExtendedKeyManager() {
            private String alias(String type) { return type != null && type.startsWith("EC") ? ALIAS : null; }
            public String[] getClientAliases(String type, Principal[] issuers) { return null; }
            public String chooseClientAlias(String[] types, Principal[] issuers, Socket socket) { return null; }
            public String[] getServerAliases(String type, Principal[] issuers) { return alias(type) == null ? null : new String[]{ALIAS}; }
            public String chooseServerAlias(String type, Principal[] issuers, Socket socket) { return alias(type); }
            public String chooseEngineServerAlias(String type, Principal[] issuers, SSLEngine engine) { return alias(type); }
            public X509Certificate[] getCertificateChain(String name) { return ALIAS.equals(name) ? chain.clone() : null; }
            public PrivateKey getPrivateKey(String name) { return ALIAS.equals(name) ? privateKey : null; }
        };
        tls = SSLContext.getInstance("TLS");
        tls.init(new KeyManager[]{manager}, null, new SecureRandom());
    }
}
