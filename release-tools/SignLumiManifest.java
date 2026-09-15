import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.Signature;
import java.security.cert.Certificate;
import java.util.Base64;

public final class SignLumiManifest {
    public static void main(String[] args) throws Exception {
        if (args.length != 6) throw new IllegalArgumentException("keystore storepass alias keypass manifest signature");
        Path keystore = Path.of(args[0]);
        char[] storePass = args[1].toCharArray();
        String alias = args[2];
        char[] keyPass = args[3].toCharArray();
        Path manifest = Path.of(args[4]);
        Path out = Path.of(args[5]);

        KeyStore ks = KeyStore.getInstance("JKS");
        try (InputStream in = Files.newInputStream(keystore)) { ks.load(in, storePass); }
        PrivateKey key = (PrivateKey) ks.getKey(alias, keyPass);
        Certificate cert = ks.getCertificate(alias);
        if (key == null || cert == null) throw new SecurityException("Signing identity is unavailable");

        byte[] data = Files.readAllBytes(manifest);
        Signature signer = Signature.getInstance("SHA256withRSA");
        signer.initSign(key);
        signer.update(data);
        byte[] signature = signer.sign();

        Signature verifier = Signature.getInstance("SHA256withRSA");
        verifier.initVerify(cert);
        verifier.update(data);
        if (!verifier.verify(signature)) throw new SecurityException("Manifest signature self-check failed");

        Files.writeString(out, Base64.getEncoder().encodeToString(signature) + "\n");
    }
}
